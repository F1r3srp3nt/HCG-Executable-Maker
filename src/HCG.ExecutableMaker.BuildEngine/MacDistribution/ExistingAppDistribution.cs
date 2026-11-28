using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.MacDistribution;

/// <summary>What to do with an already-built macOS .app (for example a .NET app built and tested by CI, unsigned).</summary>
public sealed record ExistingAppRequest
{
    /// <summary>The built .app. It is never changed: a copy is signed.</summary>
    public required string InputApp { get; init; }
    /// <summary>Where the signed copy, the ZIP, the .pkg, checksums, passports and the report go. Must not be inside the input.</summary>
    public required string OutputDirectory { get; init; }
    /// <summary>SHA-1 of the Developer ID Application identity in the keychain. Null for an ad-hoc test build.</summary>
    public string? IdentitySha1 { get; init; }
    /// <summary>
    /// Ad-hoc test build (<c>codesign --sign -</c>, hardened runtime, no timestamp): the same layout and signature checks
    /// without a certificate, never notarized. Gatekeeper rejects it on every other Mac: for build machines only.
    /// </summary>
    public bool AdHocTest { get; init; }
    /// <summary>Entitlements plist signed into the app as written (boolean true entitlements only). Null = none.</summary>
    public string? EntitlementsFile { get; init; }
    /// <summary>The person reviewed the entitlements read from the plist.</summary>
    public bool EntitlementsConfirmed { get; init; }
    public bool MakePkg { get; init; }
    /// <summary>SHA-1 of the Developer ID Installer identity; null = unsigned package (local testing only).</summary>
    public string? InstallerSha1 { get; init; }
    /// <summary>Release version (1.0.0-beta.2) when more precise than CFBundleShortVersionString; its numeric part must match.</summary>
    public string? ReleaseVersion { get; init; }
    public string? BuildNumber { get; init; }
    /// <summary>notarytool keychain profile; null = do not notarize now.</summary>
    public string? NotaryKeychainProfile { get; init; }
    /// <summary>The app's source checkout, for the passport's commit (unknown without it; never guessed).</summary>
    public string? SourceRepository { get; init; }
    public string Provider { get; init; } = "executable-maker-local";
}

/// <summary>Everything checked before anything is copied or signed.</summary>
public sealed record ExistingAppPlan
{
    public required MacAppFacts Facts { get; init; }
    public SigningIdentity? Identity { get; init; }
    public SigningIdentity? Installer { get; init; }
    public string? EntitlementsPlist { get; init; }
    public IReadOnlyList<string> Entitlements { get; init; } = [];
    public required string OutputApp { get; init; }
    public required string ZipPath { get; init; }
    public string? PkgPath { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanRun => Problems.Count == 0;
}

public sealed record ExistingAppResult(IReadOnlyList<DistributionStep> Steps, string? SignedApp, IReadOnlyList<string> Files, IReadOnlyList<string> Passports, string? Report)
{
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Status is not VerificationStatus.Failed);
}

/// <summary>
/// Sign an existing app: copy the .app (the input is never modified), sign every Mach-O inside out with the hardened
/// runtime (Developer ID with a secure timestamp, or ad hoc for a test build), sign the bundle with the confirmed
/// entitlements, verify, optionally notarize and staple, zip the (stapled) app, optionally build a .pkg (signed with
/// Developer ID Installer and notarized), then checksums and a Release Passport per file. Each step is reported on its
/// own. Private keys never leave the keychain; notary credentials are a keychain profile name; nothing is published.
/// </summary>
public sealed class ExistingAppDistribution(IProcessRunner runner, string logsDirectory, bool? isMac = null, string codesign = "/usr/bin/codesign")
{
    public const string TestSuffix = "-adhoc-TEST-not-for-distribution";
    private readonly bool _isMac = isMac ?? OperatingSystem.IsMacOS();

    // ───────────── Planning (pure: no process is started, nothing is written) ─────────────

    /// <summary>Reads an entitlements plist: its text, its boolean keys, or why it cannot be signed in.</summary>
    public static (string? Plist, IReadOnlyList<string> Keys, string? Problem) ReadEntitlements(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return (null, [], null);
        if (!File.Exists(file)) return (null, [], $"The entitlements file {file} does not exist.");
        byte[] bytes;
        Dictionary<string, object> dict;
        try { bytes = File.ReadAllBytes(file); dict = PlistReader.ParseDictionary(bytes); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or FormatException)
        {
            return (null, [], $"The entitlements file {Path.GetFileName(file)} is not a property list: {ex.Message}");
        }
        if (dict.GetValueOrDefault(Packaging.MacDistribution.Entitlements.GetTaskAllow) is true)
            return (null, [], "The entitlements include com.apple.security.get-task-allow (debugging): the notary service rejects it. Remove it for distribution.");
        var other = dict.Where(kv => kv.Value is not true).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        if (other.Count > 0)
            return (null, [], "Only entitlements set to true are supported here; these have another value: " + string.Join(", ", other) + ".");
        // Signed exactly as written in the file the person reviewed (XML or binary plists are rewritten as XML with the same keys).
        var text = Encoding.UTF8.GetString(bytes);
        var plist = text.Contains("<plist", StringComparison.Ordinal) ? text : Packaging.MacDistribution.Entitlements.ToPlist(dict.Keys);
        return (plist, dict.Keys.Order(StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>Names of the distribution files (test builds say so in their names).</summary>
    public static (string Zip, string Pkg) FileNames(MacAppFacts facts, string? releaseVersion, bool test)
    {
        var name = PathGuard.SanitizeFileName(Path.GetFileNameWithoutExtension(facts.BundlePath.TrimEnd('/', Path.DirectorySeparatorChar)), "Application");
        var version = string.IsNullOrWhiteSpace(releaseVersion) ? facts.ShortVersion ?? "0" : releaseVersion.Trim();
        var arch = facts.Architectures.Distinct().Count() > 1 ? "universal" : facts.Architectures.FirstOrDefault() switch { "x86_64" => "x64", { } a => a, null => "unknown" };
        return test
            ? ($"{name}-{version}-osx-{arch}{TestSuffix}.app.zip", $"{name}-{version}-osx-{arch}{TestSuffix}.pkg")
            : ($"{name}.app.zip", $"{name}-{version}-osx-{arch}.pkg");
    }

    /// <summary>Checks the request against the app, the keychain inventory and this host. Nothing is changed.</summary>
    public ExistingAppPlan Plan(ExistingAppRequest request, MacSigningInventory inventory)
    {
        var input = Path.GetFullPath(request.InputApp).TrimEnd('/', Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(request.OutputDirectory).TrimEnd('/', Path.DirectorySeparatorChar);
        var problems = new List<string>();
        var warnings = new List<string>();
        if (!_isMac) problems.Add("Signing an existing app runs on a Mac (codesign, ditto, pkgbuild, notarytool).");
        MacAppFacts facts;
        if (!input.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(input))
        {
            problems.Add($"{request.InputApp} is not a macOS .app bundle.");
            facts = new MacAppFacts { BundlePath = input, Problem = "not an .app" };
        }
        else
        {
            facts = MacAppFacts.Read(input);
            if (facts.Problem is { } p) problems.Add($"{Path.GetFileName(input)}: {p}");
        }
        var sep = Path.DirectorySeparatorChar;
        if (output == input || (output + sep).StartsWith(input + sep, StringComparison.Ordinal))
            problems.Add("The output folder must be outside the app: the input .app is never changed.");
        var outputApp = Path.Combine(output, Path.GetFileName(input));
        if (outputApp == input) problems.Add("The output folder is the folder that holds the input app; choose another one, so the input .app is not overwritten.");
        else if (Directory.Exists(outputApp)) problems.Add($"{outputApp} already exists. Choose an empty output folder or move it away (HCG does not overwrite it).");

        SigningIdentity? identity = null, installer = null;
        if (request.AdHocTest)
        {
            if (request.IdentitySha1 is not null) problems.Add("An ad-hoc test build uses no certificate; clear the Developer ID identity or turn off the test build.");
            if (request.NotaryKeychainProfile is not null) problems.Add("Test builds are not notarized: the notary service accepts only Developer ID signatures.");
            if (request.InstallerSha1 is not null) problems.Add("A test build's package stays unsigned: a Developer ID Installer signature around an ad-hoc app cannot be notarized.");
        }
        else if (request.IdentitySha1 is null)
            problems.Add("Choose a Developer ID Application identity (or make an ad-hoc test build, which must not be distributed).");
        else if (inventory.Find(request.IdentitySha1) is not { } id)
            problems.Add($"The identity {request.IdentitySha1} is not in the keychain. " + MacDistributionCatalog.CertificatesHelp);
        else if (id.Kind != AppleCertificateKind.DeveloperIdApplication)
            problems.Add($"{id.Name} is not a Developer ID Application identity; distribution outside the Mac App Store needs one.");
        else identity = id;

        if (request.MakePkg && request.InstallerSha1 is { } isha && !request.AdHocTest)
        {
            if (inventory.Find(isha) is not { } inst) problems.Add($"The installer identity {isha} is not in the keychain. " + MacDistributionCatalog.CertificatesHelp);
            else if (inst.Kind != AppleCertificateKind.DeveloperIdInstaller) problems.Add($"{inst.Name} is not a Developer ID Installer identity.");
            else if (identity is not null && inst.TeamId != identity.TeamId) problems.Add($"The installer identity ({inst.Name}) belongs to another team than {identity.Name}.");
            else installer = inst;
        }
        if (!request.MakePkg && request.InstallerSha1 is not null) warnings.Add("An installer identity was chosen, but no .pkg is made.");
        if (request.MakePkg && request.InstallerSha1 is null && !request.AdHocTest)
            warnings.Add("The .pkg will be unsigned: for local testing only; it is not notarized. Choose a Developer ID Installer identity to distribute it.");
        if (request.NotaryKeychainProfile is { } profile && string.IsNullOrWhiteSpace(profile)) problems.Add("The notarization keychain profile name is empty.");

        var (plist, keys, entProblem) = ReadEntitlements(request.EntitlementsFile);
        if (entProblem is not null) problems.Add(entProblem);
        else if (!request.EntitlementsConfirmed)
            problems.Add(keys.Count == 0 ? "Confirm that the app is signed without entitlements (no entitlements file chosen)." : "Review the entitlements read from the plist and confirm them.");
        if (keys.Contains(Packaging.MacDistribution.Entitlements.AppSandbox))
            warnings.Add("The entitlements turn on the App Sandbox. A Developer ID app need not be sandboxed; sandboxing changes where the app keeps its data.");

        if (facts.Problem is null && Directory.Exists(input))
        {
            var data = MacBundleLayout.DataInMacOS(input, facts);
            if (data.Count > 0)
                problems.Add($"Contents/MacOS holds files that are not code ({string.Join(", ", data.Take(5))}{(data.Count > 5 ? $" and {data.Count - 5} more" : "")}). " +
                             MacBundleLayout.DataInMacOSAdvice);
            if (MacBundleLayout.LooksLikeDotNet(input, facts) && !keys.Contains("com.apple.security.cs.allow-jit"))
                warnings.Add("This is a .NET app: under the hardened runtime CoreCLR compiles code just in time and needs com.apple.security.cs.allow-jit. Without it the app stops at start-up.");
        }
        if (MacReleasePassportWriter.VersionProblem(request.ReleaseVersion, facts.ShortVersion) is { } vp) problems.Add(vp);

        var (zip, pkg) = FileNames(facts, request.ReleaseVersion, request.AdHocTest);
        return new ExistingAppPlan
        {
            Facts = facts, Identity = identity, Installer = installer, EntitlementsPlist = plist, Entitlements = keys,
            OutputApp = outputApp, ZipPath = Path.Combine(output, zip), PkgPath = request.MakePkg ? Path.Combine(output, pkg) : null,
            Problems = problems, Warnings = warnings,
        };
    }

    // ───────────── Running ─────────────

    public async Task<ExistingAppResult> RunAsync(ExistingAppRequest request, MacSigningInventory inventory, Action<string> log, CancellationToken ct)
    {
        var steps = new List<DistributionStep>();
        var plan = Plan(request, inventory);
        var facts = plan.Facts;
        void Add(string name, VerificationStatus status, string detail)
        {
            steps.Add(new(name, status, detail));
            log($"{(status == VerificationStatus.Failed ? "FAIL" : status == VerificationStatus.Passed ? "PASS" : status.ToString().ToUpperInvariant())}  {name} — {detail}");
        }
        ExistingAppResult Done(IReadOnlyList<string>? files = null, IReadOnlyList<string>? passports = null, string? report = null) =>
            new(steps, Directory.Exists(plan.OutputApp) ? plan.OutputApp : null, files ?? [], passports ?? [], report);

        if (facts.Problem is null)
            Add("Inspect", VerificationStatus.Passed, $"{facts.BundleIdentifier} {facts.ShortVersion} ({facts.BuildVersion}), {string.Join("/", facts.Architectures)}, {facts.MachOFiles.Count} Mach-O file(s), minimum macOS {facts.MinimumSystemVersion ?? "not set"}.");
        foreach (var w in plan.Warnings) Add("Prerequisites", VerificationStatus.PassedWithWarnings, w);
        if (!plan.CanRun)
        {
            foreach (var p in plan.Problems) Add("Prerequisites", VerificationStatus.Failed, p);
            return Done();
        }
        var test = request.AdHocTest;
        var identity = test ? "-" : plan.Identity!.Sha1;
        var plog = new Log(log);
        var signer = new MacCodeSigner(runner, codesign);

        // 1. Copy: the input is never modified. Build-machine metadata (quarantine, Finder info) is not part of the app and breaks codesign.
        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputApp)!);
        var copy = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = [Path.GetFullPath(request.InputApp), plan.OutputApp], Timeout = TimeSpan.FromMinutes(10) },
            (_, l) => log(l), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!copy.Succeeded || !Directory.Exists(plan.OutputApp)) { Add("Copy", VerificationStatus.Failed, $"ditto could not copy the app (exit code {copy.ExitCode})."); return Done(); }
        var xattr = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xattr", Arguments = ["-cr", plan.OutputApp], Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
        Add("Copy", xattr.Result.Succeeded ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings,
            $"{Path.GetFileName(plan.OutputApp)} copied to {Path.GetDirectoryName(plan.OutputApp)}" + (xattr.Result.Succeeded ? "; extended attributes cleared." : $"; xattr -cr failed ({xattr.StdErr.Trim()})."));

        // 2. Sign inside out, then the bundle (which signs the main executable with the entitlements).
        var nested = MacBundleLayout.SigningOrder(plan.OutputApp, facts);
        try
        {
            foreach (var path in nested)
                await signer.SignHardenedAsync(path, identity, null, plog, ct).ConfigureAwait(false);
            await signer.SignHardenedAsync(plan.OutputApp, identity, plan.EntitlementsPlist, plog, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { Add("Sign", VerificationStatus.Failed, ex.Message); return Done(); }
        Add("Sign", VerificationStatus.Passed, (test ? "Ad hoc (test build: no certificate, no timestamp)" : $"{plan.Identity!.Name}, secure timestamp") +
            $", hardened runtime; {nested.Count} nested item(s) inside out, then the bundle with {(plan.Entitlements.Count == 0 ? "no entitlements" : string.Join(", ", plan.Entitlements))}.");

        // 3. Verification of the copy, independently of the signing step.
        var checks = new List<VerificationCheck>();
        if (!test) checks.AddRange(await signer.VerifyDeveloperIdAsync(plan.OutputApp, plan.Identity!, plan.Entitlements, ct).ConfigureAwait(false));
        else
        {
            var (ok, detail) = await signer.VerifyStrictAsync(plan.OutputApp, ct).ConfigureAwait(false);
            checks.Add(new("Code signature", ok ? VerificationStatus.Passed : VerificationStatus.Failed, ok ? "codesign --verify --strict --deep passed." : "codesign --verify --strict --deep failed: " + detail));
            var info = await signer.DescribeAsync(plan.OutputApp, ct).ConfigureAwait(false);
            checks.Add(info is { AdHoc: true }
                ? new("Signature kind", VerificationStatus.PassedWithWarnings, "Ad hoc (test build): not Developer ID; Gatekeeper rejects it on other Macs. Do not distribute.")
                : new("Signature kind", VerificationStatus.Failed, "Expected an ad-hoc signature; codesign -d reads " + (info?.Leaf ?? "none") + "."));
            checks.Add(info?.HardenedRuntime == true
                ? new("Hardened runtime", VerificationStatus.Passed, "Enabled (flags include runtime).")
                : new("Hardened runtime", VerificationStatus.Failed, "Not enabled."));
            checks.Add(await signer.EntitlementsCheckAsync(plan.OutputApp, plan.Entitlements, requireSandbox: false, ct).ConfigureAwait(false));
        }
        var nestedBad = new List<string>();
        foreach (var path in nested)
        {
            var n = await signer.DescribeAsync(path, ct).ConfigureAwait(false);
            var name = Path.GetRelativePath(plan.OutputApp, path).Replace('\\', '/');
            if (n is null) nestedBad.Add(name + " (unsigned)");
            else if (!n.HardenedRuntime) nestedBad.Add(name + " (no hardened runtime)");
            else if (!test && n.TeamId != plan.Identity!.TeamId) nestedBad.Add($"{name} (team {n.TeamId ?? "none"})");
        }
        checks.Add(nestedBad.Count == 0
            ? new("Nested code", VerificationStatus.Passed, nested.Count == 0 ? "No nested code." : $"All {nested.Count} nested item(s): hardened runtime{(test ? "" : ", team " + plan.Identity!.TeamId)}.")
            : new("Nested code", VerificationStatus.Failed, "Not signed as required: " + string.Join(", ", nestedBad)));
        foreach (var c in checks) Add(c.Name, c.Status, c.Detail);
        if (checks.Any(c => c.Status == VerificationStatus.Failed)) return Done();

        // 4. Notarization of the app (explicit, Developer ID only), stapled before it is zipped.
        if (request.NotaryKeychainProfile is { } profile)
        {
            var notary = await new NotaryClient(runner, logsDirectory).NotarizeAsync(plan.OutputApp, PackagingFormat.MacAppBundle, profile, log, ct).ConfigureAwait(false);
            foreach (var s in notary) Add("App: " + s.Name, s.Status, s.Detail);
            if (notary.Count == 0 || notary.Any(s => s.Status == VerificationStatus.Failed)) return Done();
        }
        else
        {
            var gk = await signer.GatekeeperAsync(plan.OutputApp, "execute", ct).ConfigureAwait(false);
            Add("Gatekeeper", test ? VerificationStatus.PassedWithWarnings : gk.Status,
                test ? $"Recorded: {gk.Detail} A test build is expected to be rejected." : gk.Detail);
            Add("Notarization", VerificationStatus.NotVerifiable, test ? "Test build: not notarized (the notary service accepts only Developer ID)." : "Not notarized now: run again with a notarization keychain profile before distributing.");
        }

        // 5. Distribution files: the ZIP of the (stapled) app, then the optional installer package.
        var files = new List<string>();
        if (File.Exists(plan.ZipPath)) File.Delete(plan.ZipPath);
        var zip = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--sequesterRsrc", "--keepParent", plan.OutputApp, plan.ZipPath], Timeout = TimeSpan.FromMinutes(10) },
            (_, _) => { }, ct).ConfigureAwait(false);
        if (!zip.Succeeded || !File.Exists(plan.ZipPath)) { Add("ZIP", VerificationStatus.Failed, $"ditto could not zip the app (exit code {zip.ExitCode})."); return Done(); }
        files.Add(plan.ZipPath);
        Add("ZIP", VerificationStatus.Passed, $"{Path.GetFileName(plan.ZipPath)} from the {(request.NotaryKeychainProfile is null ? "signed" : "stapled")} app.");

        if (plan.PkgPath is { } pkgPath)
        {
            var version = string.IsNullOrWhiteSpace(request.ReleaseVersion) ? facts.ShortVersion ?? "1.0.0" : request.ReleaseVersion.Trim();
            IReadOnlyList<VerificationCheck> pkgChecks;
            try { pkgChecks = await new MacPkgPackager(runner).PackageAppAsync(plan.OutputApp, pkgPath, facts.Label, version, plan.Installer?.Name, plog, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { Add("Package", VerificationStatus.Failed, ex.Message); return Done(files); }
            foreach (var c in pkgChecks.Where(c => c.Name != "Installation test")) Add("Package: " + c.Name, c.Status, c.Detail);
            if (pkgChecks.Any(c => c.Status == VerificationStatus.Failed)) return Done(files);
            files.Add(pkgPath);
            if (request.NotaryKeychainProfile is { } p2 && plan.Installer is not null)
            {
                var notary = await new NotaryClient(runner, logsDirectory).NotarizeAsync(pkgPath, PackagingFormat.MacPkg, p2, log, ct).ConfigureAwait(false);
                foreach (var s in notary) Add("Package: " + s.Name, s.Status, s.Detail);
                if (notary.Count == 0 || notary.Any(s => s.Status == VerificationStatus.Failed)) return Done(files);
            }
            else if (request.NotaryKeychainProfile is not null)
                Add("Package: Notarization", VerificationStatus.NotVerifiable, "An unsigned package is not notarized.");
        }

        // 6. Checksums and a Release Passport per file (the final bytes, after stapling).
        var output = Path.GetDirectoryName(plan.ZipPath)!;
        var sums = new StringBuilder();
        foreach (var f in files) sums.Append(await Checksums.Sha256FileAsync(f, ct).ConfigureAwait(false)).Append("  ").Append(Path.GetFileName(f)).Append('\n');
        var sumsFile = Path.Combine(output, test ? $"SHA256SUMS{TestSuffix}.txt" : "SHA256SUMS.txt");
        await File.WriteAllTextAsync(sumsFile, sums.ToString(), ct).ConfigureAwait(false);
        Add("Checksums", VerificationStatus.Passed, $"{Path.GetFileName(sumsFile)} ({files.Count} file(s)).");

        var passports = new List<string>();
        var doctor = await new MacSigningDoctor(runner, codesign).InspectAsync(plan.OutputApp, ct).ConfigureAwait(false);
        var source = request.SourceRepository is { } repo && FindGit() is { } git
            ? await SourceProvenance.ReadAsync(runner, git, Path.GetFullPath(repo), ct).ConfigureAwait(false)
            : SourceProvenance.Unknown;
        var issuer = "HCG Executable Maker " + HostInfo.ProductVersion;
        foreach (var f in files)
        {
            var r = await MacReleasePassportWriter.CreateAsync(new MacReleasePassportWriter.Input(plan.OutputApp, f, MacAppFacts.Read(plan.OutputApp), doctor, source,
                request.Provider, issuer, request.BuildNumber, request.ReleaseVersion, issuer), ct).ConfigureAwait(false);
            if (r.Passport is null) { Add("Release Passport", VerificationStatus.Failed, $"{Path.GetFileName(f)}: " + string.Join("; ", r.Problems)); continue; }
            passports.Add(await MacReleasePassportWriter.WriteAsync(r.Passport, f, null, ct).ConfigureAwait(false));
            Add("Release Passport", VerificationStatus.Passed, $"{Path.GetFileName(f)}.release-passport.json: version {r.Version}, SHA-256 {r.Sha256[..12]}…, source {source.Commit ?? "unknown"}.");
        }

        var report = Path.Combine(output, Path.GetFileNameWithoutExtension(plan.OutputApp) + (test ? TestSuffix : "") + ".signing-report.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            format = "hcg.executable-maker.existing-app-signing", formatVersion = 1, mode = test ? "adhoc-test" : "developer-id",
            input = Path.GetFullPath(request.InputApp), signedApp = plan.OutputApp, identity = plan.Identity?.Name, installer = plan.Installer?.Name,
            entitlements = plan.Entitlements, releaseVersion = request.ReleaseVersion, notarized = request.NotaryKeychainProfile is not null,
            steps = steps.Select(s => new { name = s.Name, status = s.Status.ToString(), detail = s.Detail }),
            files = files.Select(Path.GetFileName), passports = passports.Select(Path.GetFileName),
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n", ct).ConfigureAwait(false);
        return Done(files, passports, report);
    }

    private static string? FindGit() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "git")).FirstOrDefault(File.Exists);

    private sealed class Log(Action<string> log) : IPackagingLog
    {
        public void Info(string message) => log(message);
        public void Warning(string message) => log("Warning: " + message);
    }
}
