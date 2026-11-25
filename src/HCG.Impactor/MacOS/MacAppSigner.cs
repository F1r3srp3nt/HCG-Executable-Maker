using System.Security.Cryptography;
using HCG.ExecutableMaker.Core;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Inspection;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;

namespace HCG.Impactor.MacOS;

/// <summary>
/// Signs an IPA on macOS with Apple's own tools: <c>ditto</c> (extract/package, keeping symlinks and permissions),
/// <c>PlistBuddy</c> (bundle identifier changes) and <c>codesign</c> with a Keychain identity chosen by SHA-1 — the
/// private key never leaves the Keychain and HCG never reads it. Bundles are signed innermost first, each app or
/// extension with its own provisioning profile and entitlements; the result is verified with
/// <c>codesign --verify --deep --strict</c> and inspected again before it is reported as signed.
/// The source IPA is only read; all work happens in a private temporary folder.
/// </summary>
public sealed class MacAppSigner(IProcessRunner runner, string? workRoot = null) : IAppSigner
{
    private const string Codesign = "/usr/bin/codesign";
    private const string Ditto = "/usr/bin/ditto";
    private const string PlistBuddy = "/usr/libexec/PlistBuddy";

    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(Codesign) && File.Exists(Ditto);

    public async Task<SignResult> SignAsync(SignJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        void Say(string line) { log.Add(line); progress?.Report(line); }
        SignResult Fail(string output, bool cancelled = false, bool timedOut = false, string? work = null) =>
            new() { Succeeded = false, Failure = InstallDiagnostics.Diagnose(output, cancelled, timedOut, toolMissing: !IsAvailable), Log = log, WorkDirectory = work };

        if (!IsAvailable) return Fail("codesign/ditto not available on this computer.");
        if (!job.Plan.IsReady) return Fail("The signing plan has unresolved blockers: " + string.Join("; ", job.Plan.Findings.Blockers().Select(b => b.Title)));
        var source = Path.GetFullPath(job.SourceIpa);
        var output = Path.GetFullPath(job.OutputIpa);
        if (string.Equals(source, output, StringComparison.Ordinal)) return Fail("The output must be a new file; HCG never overwrites the original IPA.");
        if (File.Exists(output)) return Fail($"{output} already exists. Choose another name; HCG does not overwrite files.");

        var work = Path.Combine(workRoot ?? Path.GetTempPath(), "hcg-impactor-sign-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // 1. Extract a private copy.
            Say($"Extracting {Path.GetFileName(source)} …");
            var x = await Run(Ditto, ["-x", "-k", source, work], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (!x.Ok) return Fail(x.Output, x.Cancelled, x.TimedOut, work);
            var payload = Path.Combine(work, "Payload");
            var app = Directory.Exists(payload) ? Directory.GetDirectories(payload, "*.app").FirstOrDefault() : null;
            if (app is null) return Fail("No Payload/<Name>.app after extraction.", work: work);

            var identity = job.Plan.Identity!;
            var n = 0;
            foreach (var a in job.Plan.Assignments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = a.Bundle.RelativePath.Length == 0 ? app : Path.Combine(app, a.Bundle.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) && !Directory.Exists(path)) return Fail($"{a.Label} is missing from the extracted app.", work: work);

                // 2. Bundle identifier changes (main app and extensions under it).
                if (a.NewBundleIdentifier is { } newId && a.Bundle.TakesEntitlements)
                {
                    Say($"{a.Label}: bundle identifier → {newId}");
                    var pb = await Run(PlistBuddy, ["-c", $"Set :CFBundleIdentifier {newId}", Path.Combine(path, "Info.plist")], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    if (!pb.Ok) return Fail(pb.Output, pb.Cancelled, pb.TimedOut, work);
                }

                var args = new List<string> { "--force", "--sign", identity.Sha1, "--timestamp=none" };
                if (a.Bundle.TakesEntitlements)
                {
                    // 3. Embed the provisioning profile and write the entitlements for this bundle.
                    if (a.Profile?.Path is not { } profilePath || !File.Exists(profilePath)) return Fail($"{a.Label}: the provisioning profile file is not available.", work: work);
                    File.Copy(profilePath, Path.Combine(path, "embedded.mobileprovision"), overwrite: true);
                    var ent = Path.Combine(work, $"entitlements-{n++}.plist");
                    await File.WriteAllTextAsync(ent, PlistXmlWriter.Write(a.Entitlements ?? new Dictionary<string, object>()), cancellationToken).ConfigureAwait(false);
                    args.AddRange(["--entitlements", ent, "--generate-entitlement-der"]);
                    Say($"{a.Label}: profile \"{a.Profile.Info.Name}\", {a.Entitlements?.Count ?? 0} entitlement(s)");
                }
                else if (a.Bundle.Kind == BundleKind.Framework)
                    DeleteIfExists(Path.Combine(path, "embedded.mobileprovision"));
                args.Add(path);

                // 4. Sign (innermost first — the plan's order).
                Say($"Signing {a.Label} with {identity.CommonName} …");
                var s = await Run(Codesign, args, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                if (!s.Ok) return Fail(s.Output, s.Cancelled, s.TimedOut, work);
            }

            // 5. Verify exactly as Apple's tools see it.
            Say("Verifying the signature (codesign --verify --deep --strict) …");
            var v = await Run(Codesign, ["--verify", "--deep", "--strict", "--verbose=2", app], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (!v.Ok) return Fail("Signature verification failed after signing:\n" + v.Output, v.Cancelled, v.TimedOut, work);

            // 6. Package as a new IPA (Payload/<Name>.app at the root).
            Say($"Writing {Path.GetFileName(output)} …");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var c = await Run(Ditto, ["-c", "-k", "--sequesterRsrc", "--keepParent", payload, output], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (!c.Ok) return Fail(c.Output, c.Cancelled, c.TimedOut, work);

            string sha;
            await using (var fs = File.OpenRead(output)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            Say("Inspecting the signed IPA …");
            var re = IpaInspector.Inspect(output, ct: cancellationToken);
            Say($"Signed: {output} (SHA-256 {sha[..16]}…)");
            return new SignResult
            {
                Succeeded = true, OutputIpa = output, OutputSha256 = sha, SignedAppDirectory = app, WorkDirectory = work,
                Verification = v.Output.Trim(), Reinspection = re, Log = log,
            };
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(output);
            return Fail("", cancelled: true, work: work);
        }
    }

    public async Task<SignResult> UnpackAsync(string ipa, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return new SignResult { Failure = InstallDiagnostics.Diagnose("", toolMissing: true) };
        var work = Path.Combine(workRoot ?? Path.GetTempPath(), "hcg-impactor-sign-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        var x = await Run(Ditto, ["-x", "-k", Path.GetFullPath(ipa), work], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var app = Directory.Exists(Path.Combine(work, "Payload")) ? Directory.GetDirectories(Path.Combine(work, "Payload"), "*.app").FirstOrDefault() : null;
        if (!x.Ok || app is null)
            return new SignResult { WorkDirectory = work, Failure = InstallDiagnostics.Diagnose(x.Ok ? "No Payload/<Name>.app in the IPA." : x.Output, x.Cancelled, x.TimedOut) };
        return new SignResult { Succeeded = true, OutputIpa = Path.GetFullPath(ipa), SignedAppDirectory = app, WorkDirectory = work };
    }

    public void Cleanup(SignResult result)
    {
        if (result.WorkDirectory is { } w && Path.GetFileName(w).StartsWith("hcg-impactor-sign-", StringComparison.Ordinal)) IpaInspector.TryDelete(w);
    }

    private sealed record Step(bool Ok, string Output, bool Cancelled, bool TimedOut);

    private async Task<Step> Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = args, Timeout = timeout }, ct).ConfigureAwait(false);
        var text = $"$ {Path.GetFileName(exe)} {string.Join(' ', args.Select(Quote))}\n{stdout}{stderr}";
        return new Step(r.Succeeded, text, r.Cancelled, r.TimedOut);
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
