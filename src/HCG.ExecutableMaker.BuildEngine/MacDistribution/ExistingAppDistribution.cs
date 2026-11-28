using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// The built .app, or a .zip that holds exactly one .app at its top level (for example the signing input a CI run
    /// published). It is never changed: a copy (or the ZIP's extracted app) is signed.
    /// </summary>
    public required string InputApp { get; init; }
    /// <summary>
    /// Optional checksum list (SHA256SUMS style, "&lt;sha256&gt;  &lt;file&gt;") that must name the input ZIP with its SHA-256;
    /// a mismatch stops before anything is extracted or signed.
    /// </summary>
    public string? InputChecksums { get; init; }
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
    /// <summary>A compressed disk image with the app and an Applications link, made from the (stapled) app.</summary>
    public bool MakeDmg { get; init; }
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
    public string? DmgPath { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanRun => Problems.Count == 0;
}

public sealed record ExistingAppResult(IReadOnlyList<DistributionStep> Steps, string? SignedApp, IReadOnlyList<string> Files, IReadOnlyList<string> Passports, string? Report)
{
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Status is not VerificationStatus.Failed);
    /// <summary>
    /// The files that may be handed to Ship to Store (a private draft): Developer ID signed, notarized, stapled and
    /// accepted by Gatekeeper as a customer receives them (the ZIP's app after extracting it). Never a test build.
    /// </summary>
    public IReadOnlyList<string> ReadyForStore { get; init; } = [];
    /// <summary>Why no (or not every) file is ready for Ship to Store, in words.</summary>
    public string? StoreNote { get; init; }
}

/// <summary>
/// Sign an existing app: copy the .app (the input is never modified), sign every Mach-O inside out with the hardened
/// runtime (Developer ID with a secure timestamp, or ad hoc for a test build), sign the bundle with the confirmed
/// entitlements, verify, optionally notarize and staple, zip the (stapled) app, optionally build a .pkg (signed with
/// Developer ID Installer and notarized), then checksums and a Release Passport per file. Each step is reported on its
/// own. Private keys never leave the keychain; notary credentials are a keychain profile name; nothing is published.
/// </summary>
public sealed partial class ExistingAppDistribution(IProcessRunner runner, string logsDirectory, bool? isMac = null, string codesign = "/usr/bin/codesign", string? tempDirectory = null)
{
    public const string TestSuffix = "-adhoc-TEST-not-for-distribution";
    private const string Ditto = "/usr/bin/ditto";
    private const string Hdiutil = "/usr/bin/hdiutil";
    private readonly bool _isMac = isMac ?? OperatingSystem.IsMacOS();
    private readonly string _temp = tempDirectory ?? Path.GetTempPath();

    /// <summary>True for an input that is a ZIP holding the app (rather than the .app folder).</summary>
    public static bool IsZipInput(string input) => input.TrimEnd('/', Path.DirectorySeparatorChar).EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

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
    public static (string Zip, string Pkg, string Dmg) FileNames(MacAppFacts facts, string? releaseVersion, bool test)
    {
        var name = PathGuard.SanitizeFileName(Path.GetFileNameWithoutExtension(facts.BundlePath.TrimEnd('/', Path.DirectorySeparatorChar)), "Application");
        var version = string.IsNullOrWhiteSpace(releaseVersion) ? facts.ShortVersion ?? "0" : releaseVersion.Trim();
        var arch = facts.Architectures.Distinct().Count() > 1 ? "universal" : facts.Architectures.FirstOrDefault() switch { "x86_64" => "x64", { } a => a, null => "unknown" };
        return test
            ? ($"{name}-{version}-osx-{arch}{TestSuffix}.app.zip", $"{name}-{version}-osx-{arch}{TestSuffix}.pkg", $"{name}-{version}-osx-{arch}{TestSuffix}.dmg")
            : ($"{name}.app.zip", $"{name}-{version}-osx-{arch}.pkg", $"{name}-{version}-osx-{arch}.dmg");
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
        var zipInput = IsZipInput(input) && File.Exists(input);
        if (zipInput)
        {
            // The ZIP is verified (checksum list) and extracted when signing starts; its app is then planned again, with
            // every check below, before anything is signed.
            var name = Path.GetFileName(input)[..^4];
            facts = new MacAppFacts { BundlePath = Path.Combine(Path.GetDirectoryName(input)!, name.EndsWith(".app", StringComparison.Ordinal) ? name : name + ".app") };
            warnings.Add($"{Path.GetFileName(input)} is extracted and its app checked when signing starts, before anything is signed" +
                         (request.InputChecksums is null ? "." : $"; its SHA-256 must match {Path.GetFileName(request.InputChecksums)}."));
        }
        else if (!input.EndsWith(".app", StringComparison.Ordinal) || !Directory.Exists(input))
        {
            problems.Add($"{request.InputApp} is not a macOS .app bundle (or a .zip that holds one).");
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
        var outputApp = Path.Combine(output, Path.GetFileName(zipInput ? facts.BundlePath : input));
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

        if (request.InputChecksums is { } sumsFile && !zipInput && !File.Exists(sumsFile))
            problems.Add($"The checksum list {sumsFile} does not exist.");
        if (facts.Problem is null && Directory.Exists(input))
        {
            var data = MacBundleLayout.DataInMacOS(input, facts);
            if (data.Count > 0)
                problems.Add($"Contents/MacOS holds files that are not code ({string.Join(", ", data.Take(5))}{(data.Count > 5 ? $" and {data.Count - 5} more" : "")}). " +
                             MacBundleLayout.DataInMacOSAdvice);
            if (MacBundleLayout.LooksLikeDotNet(input, facts) && !keys.Contains("com.apple.security.cs.allow-jit"))
                warnings.Add("This is a .NET app: under the hardened runtime CoreCLR compiles code just in time and needs com.apple.security.cs.allow-jit. Without it the app stops at start-up.");
        }
        if (!zipInput && MacReleasePassportWriter.VersionProblem(request.ReleaseVersion, facts.ShortVersion) is { } vp) problems.Add(vp);

        var (zip, pkg, dmg) = FileNames(facts, request.ReleaseVersion, request.AdHocTest);
        return new ExistingAppPlan
        {
            Facts = facts, Identity = identity, Installer = installer, EntitlementsPlist = plist, Entitlements = keys,
            OutputApp = outputApp, ZipPath = Path.Combine(output, zip), PkgPath = request.MakePkg ? Path.Combine(output, pkg) : null,
            DmgPath = request.MakeDmg ? Path.Combine(output, dmg) : null,
            Problems = problems, Warnings = warnings,
        };
    }

    // ───────────── Running ─────────────

    public async Task<ExistingAppResult> RunAsync(ExistingAppRequest request, MacSigningInventory inventory, Action<string> log, CancellationToken ct)
    {
        var original = Path.GetFullPath(request.InputApp).TrimEnd('/', Path.DirectorySeparatorChar);
        string? staging = null;
        try
        {
            var pre = new List<DistributionStep>();
            string? digest = null;
            if (IsZipInput(original) && File.Exists(original))
            {
                // A ZIP is checked against its checksum list, then extracted into a staging folder of this run only; the
                // ZIP itself is only read. The staging folder is removed whatever happens (also when reading throws).
                staging = Path.Combine(_temp, "hcg-existing-app-" + Guid.NewGuid().ToString("N")[..10]);
                var (app, sha, checks) = await StageZipAsync(original, request.InputChecksums, staging, ct).ConfigureAwait(false);
                digest = sha;
                foreach (var c in checks)
                {
                    pre.Add(new(c.Name, c.Status, c.Detail));
                    log($"{(c.Status == VerificationStatus.Failed ? "FAIL" : "PASS")}  {c.Name} — {c.Detail}");
                }
                if (app is null) return new ExistingAppResult(pre, null, [], [], null);
                request = request with { InputApp = app };
            }
            else if (Directory.Exists(original))
            {
                digest = (await Checksums.Sha256DirectoryAsync(original, ct).ConfigureAwait(false)).Digest;
                if (request.InputChecksums is not null)
                    pre.Add(new("Published checksum", VerificationStatus.NotVerifiable, "A checksum list describes files; this input is a folder. Choose the ZIP the list names to compare it."));
            }
            return await RunStagedAsync(request, original, digest, pre, inventory, log, ct).ConfigureAwait(false);
        }
        finally { if (staging is not null) TryDelete(staging); }
    }

    private async Task<ExistingAppResult> RunStagedAsync(ExistingAppRequest request, string original, string? originalDigest, List<DistributionStep> pre,
        MacSigningInventory inventory, Action<string> log, CancellationToken ct)
    {
        var steps = new List<DistributionStep>(pre);
        var plan = Plan(request, inventory);
        var facts = plan.Facts;
        void Add(string name, VerificationStatus status, string detail)
        {
            steps.Add(new(name, status, detail));
            log($"{(status == VerificationStatus.Failed ? "FAIL" : status == VerificationStatus.Passed ? "PASS" : status.ToString().ToUpperInvariant())}  {name} — {detail}");
        }
        ExistingAppResult Done(IReadOnlyList<string>? files = null, IReadOnlyList<string>? passports = null, string? report = null,
            IReadOnlyList<string>? ready = null, string? storeNote = null) =>
            new(steps, Directory.Exists(plan.OutputApp) ? plan.OutputApp : null, files ?? [], passports ?? [], report)
            {
                ReadyForStore = ready ?? [], StoreNote = storeNote ?? "Not ready for Ship to Store: a step failed or did not run.",
            };
        // The original input (the .app folder, or the ZIP) is hashed again after each stage that runs tools: any change
        // stops the run. Executable Maker works on a copy, so a change means something else modified it.
        async Task<bool> OriginalUnchangedAsync(string stage)
        {
            if (originalDigest is null) return true;
            string? now = Directory.Exists(original) ? (await Checksums.Sha256DirectoryAsync(original, ct).ConfigureAwait(false)).Digest
                : File.Exists(original) ? await Checksums.Sha256FileAsync(original, ct).ConfigureAwait(false) : null;
            if (now == originalDigest) return true;
            Add("Original input", VerificationStatus.Failed, now is null
                ? $"{original} disappeared during {stage}."
                : $"{original} changed during {stage} ({originalDigest[..16]}… → {now[..16]}…). Executable Maker works on a copy, so something else modified it; stopped.");
            return false;
        }

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
        if (!await OriginalUnchangedAsync("signing").ConfigureAwait(false)) return Done();
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
            if (!await OriginalUnchangedAsync("notarization").ConfigureAwait(false)) return Done();
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
        var notarized = request.NotaryKeychainProfile is not null && !test;
        var zipAccepted = false;
        if (notarized)
        {
            // As a customer receives it: the app extracted from the final ZIP carries its ticket and Gatekeeper accepts it.
            foreach (var c in await ZipAsDownloadedAsync(plan.ZipPath, ct).ConfigureAwait(false)) Add("ZIP: " + c.Name, c.Status, c.Detail);
            zipAccepted = steps.Where(s => s.Name.StartsWith("ZIP: ", StringComparison.Ordinal)).All(s => s.Status == VerificationStatus.Passed);
            if (!zipAccepted) return Done(files);
        }

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

        if (plan.DmgPath is { } dmgPath)
        {
            IReadOnlyList<VerificationCheck> dmgChecks;
            try { dmgChecks = await CreateDmgAsync(plan.OutputApp, dmgPath, facts.Label, test ? null : plan.Identity!.Sha1, plog, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { Add("Disk image", VerificationStatus.Failed, ex.Message); return Done(files); }
            foreach (var c in dmgChecks) Add("Disk image: " + c.Name, c.Status, c.Detail);
            if (dmgChecks.Any(c => c.Status == VerificationStatus.Failed)) return Done(files);
            files.Add(dmgPath);
            if (notarized)
            {
                var notary = await new NotaryClient(runner, logsDirectory).NotarizeAsync(dmgPath, PackagingFormat.MacDmg, request.NotaryKeychainProfile!, log, ct).ConfigureAwait(false);
                foreach (var s in notary) Add("Disk image: " + s.Name, s.Status, s.Detail);
                if (notary.Count == 0 || notary.Any(s => s.Status == VerificationStatus.Failed)) return Done(files);
            }
        }
        if (!await OriginalUnchangedAsync("packaging").ConfigureAwait(false)) return Done(files);
        if (originalDigest is not null)
            Add("Original input", VerificationStatus.Passed, $"{Path.GetFileName(original)} unchanged after every stage (SHA-256 {originalDigest[..16]}…).");

        // Ship to Store gate: only Developer ID files that are notarized, stapled and accepted by Gatekeeper.
        var ready = new List<string>();
        string storeNote;
        if (test) storeNote = "Test build: ad-hoc signed and not notarized. It is never offered to Ship to Store and installs on no other Mac.";
        else if (!notarized) storeNote = "Not notarized: Ship to Store accepts only notarized, stapled files that Gatekeeper accepts. Run again with a notarization keychain profile.";
        else
        {
            bool Accepted(string prefix) => steps.Any(s => s.Name == prefix + "Gatekeeper" && s.Status == VerificationStatus.Passed)
                                            && steps.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).All(s => s.Status is not VerificationStatus.Failed);
            if (zipAccepted) ready.Add(plan.ZipPath);
            if (plan.PkgPath is { } pp && files.Contains(pp) && plan.Installer is not null && Accepted("Package: ")) ready.Add(pp);
            if (plan.DmgPath is { } dp && files.Contains(dp) && Accepted("Disk image: ")) ready.Add(dp);
            var left = files.Except(ready).Select(Path.GetFileName).ToList();
            storeNote = left.Count == 0
                ? "Every file is notarized, stapled and accepted by Gatekeeper: it can go to Ship to Store as a private draft (nothing is published)."
                : $"Ready for Ship to Store: {string.Join(", ", ready.Select(Path.GetFileName))}. Not offered: {string.Join(", ", left)} (an unsigned package is never notarized).";
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
            input = original, signedApp = plan.OutputApp, identity = plan.Identity?.Name, installer = plan.Installer?.Name,
            entitlements = plan.Entitlements, releaseVersion = request.ReleaseVersion, notarized = request.NotaryKeychainProfile is not null,
            readyForStore = ready.Select(Path.GetFileName),
            steps = steps.Select(s => new { name = s.Name, status = s.Status.ToString(), detail = s.Detail }),
            files = files.Select(Path.GetFileName), passports = passports.Select(Path.GetFileName),
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n", ct).ConfigureAwait(false);
        return Done(files, passports, report, ready, storeNote);
    }

    // ───────────── Input ZIP, the ZIP as downloaded, the disk image ─────────────

    /// <summary>Checks the ZIP against its checksum list, then extracts it (ditto keeps links, modes and attributes); exactly one .app at the top.</summary>
    private async Task<(string? App, string Sha256, IReadOnlyList<VerificationCheck> Checks)> StageZipAsync(string zip, string? checksumsFile, string staging, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var sha = await Checksums.Sha256FileAsync(zip, ct).ConfigureAwait(false);
        if (checksumsFile is not null)
        {
            var c = PublishedChecksum(zip, sha, checksumsFile);
            checks.Add(c);
            if (c.Status == VerificationStatus.Failed) return (null, sha, checks);
        }
        if (!_isMac) { checks.Add(new("Input", VerificationStatus.Failed, "Signing an existing app runs on a Mac (ditto, codesign, notarytool).")); return (null, sha, checks); }
        Directory.CreateDirectory(staging);
        var extracted = Path.Combine(staging, "input");
        var r = await runner.RunAsync(new ProcessSpec { Executable = Ditto, Arguments = ["-x", "-k", zip, extracted], Timeout = TimeSpan.FromMinutes(10) }, (_, _) => { }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var apps = r.Succeeded && Directory.Exists(extracted) ? Directory.GetDirectories(extracted, "*.app") : [];
        if (apps.Length != 1)
        {
            checks.Add(new("Input", VerificationStatus.Failed, !r.Succeeded
                ? $"ditto could not extract {Path.GetFileName(zip)} (exit code {r.ExitCode}); is it a ZIP?"
                : apps.Length == 0
                    ? $"{Path.GetFileName(zip)} holds no .app at its top level (a ZIP made with ditto -c -k --keepParent has one)."
                    : $"{Path.GetFileName(zip)} holds {apps.Length} apps ({string.Join(", ", apps.Select(Path.GetFileName))}); choose a ZIP with exactly one."));
            return (null, sha, checks);
        }
        checks.Add(new("Input", VerificationStatus.Passed, $"{Path.GetFileName(zip)}: SHA-256 {sha}; extracted {Path.GetFileName(apps[0])} (the ZIP is not modified)."));
        return (apps[0], sha, checks);
    }

    /// <summary>The input ZIP's SHA-256 against the line of a SHA256SUMS-style list that names it.</summary>
    public static VerificationCheck PublishedChecksum(string zip, string sha, string checksumsFile)
    {
        if (!File.Exists(checksumsFile)) return new("Published checksum", VerificationStatus.Failed, $"{checksumsFile} does not exist.");
        var name = Path.GetFileName(zip);
        foreach (var line in File.ReadLines(checksumsFile))
        {
            var m = ChecksumLine().Match(line.Trim());
            if (!m.Success || Path.GetFileName(m.Groups["name"].Value) != name) continue;
            return string.Equals(m.Groups["sha"].Value, sha, StringComparison.OrdinalIgnoreCase)
                ? new("Published checksum", VerificationStatus.Passed, $"{name} matches {Path.GetFileName(checksumsFile)} (SHA-256 {sha}).")
                : new("Published checksum", VerificationStatus.Failed, $"{name} is {sha}, but {Path.GetFileName(checksumsFile)} lists {m.Groups["sha"].Value.ToLowerInvariant()}: this is not the file that was published. Download it again; nothing was signed.");
        }
        return new("Published checksum", VerificationStatus.Failed, $"{Path.GetFileName(checksumsFile)} does not list {name}.");
    }

    /// <summary>Extracts the final ZIP as a customer would and checks the app inside: stapled ticket, then Gatekeeper.</summary>
    private async Task<IReadOnlyList<VerificationCheck>> ZipAsDownloadedAsync(string zip, CancellationToken ct)
    {
        var temp = Path.Combine(_temp, "hcg-zip-check-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var x = await runner.RunAsync(new ProcessSpec { Executable = Ditto, Arguments = ["-x", "-k", zip, temp], Timeout = TimeSpan.FromMinutes(10) }, (_, _) => { }, ct).ConfigureAwait(false);
            var app = x.Succeeded && Directory.Exists(temp) ? Directory.GetDirectories(temp, "*.app").FirstOrDefault() : null;
            if (app is null) return [new("Contents", VerificationStatus.Failed, $"{Path.GetFileName(zip)} holds no .app after extracting it (ditto exit code {x.ExitCode}).")];
            var (vr, vo, ve) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xcrun", Arguments = ["stapler", "validate", app], Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
            return
            [
                vr.Succeeded ? new("Stapled ticket", VerificationStatus.Passed, "The app extracted from the ZIP carries its ticket (stapler validate).")
                             : new("Stapled ticket", VerificationStatus.Failed, "The app extracted from the ZIP has no valid ticket: " + MacCodeSigner.LastLines(vo + ve, 2)),
                await new MacCodeSigner(runner, codesign).GatekeeperAsync(app, "execute", ct, expectNotarized: true).ConfigureAwait(false),
            ];
        }
        finally { TryDelete(temp); }
    }

    /// <summary>
    /// A compressed disk image (UDZO) with the app and an Applications link, made from the (stapled) app and, for
    /// Developer ID, signed with the Developer ID Application identity and a secure timestamp. A test build's image stays
    /// unsigned (its name says TEST).
    /// </summary>
    private async Task<IReadOnlyList<VerificationCheck>> CreateDmgAsync(string app, string output, string volumeName, string? identitySha1, IPackagingLog log, CancellationToken ct)
    {
        var staging = Path.Combine(_temp, "hcg-dmg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(staging);
        try
        {
            var copy = await runner.RunAsync(new ProcessSpec { Executable = Ditto, Arguments = [app, Path.Combine(staging, Path.GetFileName(app))], Timeout = TimeSpan.FromMinutes(10) }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
            if (!copy.Succeeded) throw new InvalidOperationException($"ditto could not stage the app for the disk image (exit code {copy.ExitCode}).");
            File.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications");
            ProcessResult? create = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (File.Exists(output)) File.Delete(output);
                create = await runner.RunAsync(new ProcessSpec { Executable = Hdiutil, Arguments = ["create", "-volname", volumeName, "-srcfolder", staging, "-format", "UDZO", "-fs", "HFS+", output], Timeout = TimeSpan.FromMinutes(10) },
                    (_, l) => log.Info(l), ct).ConfigureAwait(false);
                if ((create.Succeeded && File.Exists(output)) || create.Cancelled || create.TimedOut) break;
                log.Info($"hdiutil create failed (attempt {attempt} of 3, exit code {create.ExitCode}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (create is not { Succeeded: true } || !File.Exists(output)) throw new InvalidOperationException($"hdiutil create failed with exit code {create?.ExitCode}.");
            var signer = new MacCodeSigner(runner, codesign);
            var checks = new List<VerificationCheck>();
            var verify = await runner.RunAsync(new ProcessSpec { Executable = Hdiutil, Arguments = ["verify", output], Timeout = TimeSpan.FromMinutes(5) }, (_, l) => log.Info(l), ct).ConfigureAwait(false);
            checks.Add(verify.Succeeded ? new("hdiutil verify", VerificationStatus.Passed, "Checksum verified by hdiutil.") : new("hdiutil verify", VerificationStatus.Failed, $"hdiutil verify exited with {verify.ExitCode}."));
            if (identitySha1 is null)
                checks.Add(new("Disk image signature", VerificationStatus.PassedWithWarnings, "Test build: the disk image is not signed and not notarized. Do not distribute it."));
            else
            {
                await signer.SignFileAsync(output, identitySha1, log, ct).ConfigureAwait(false);
                var (ok, detail) = await signer.VerifyStrictAsync(output, ct).ConfigureAwait(false);
                checks.Add(ok ? new("Disk image signature", VerificationStatus.Passed, "Developer ID Application signature with a secure timestamp; codesign --verify passed.")
                              : new("Disk image signature", VerificationStatus.Failed, "codesign --verify failed: " + detail));
            }
            return checks;
        }
        finally { TryDelete(staging); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^(?<sha>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$")] private static partial Regex ChecksumLine();

    private static string? FindGit() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "git")).FirstOrDefault(File.Exists);

    private sealed class Log(Action<string> log) : IPackagingLog
    {
        public void Info(string message) => log(message);
        public void Warning(string message) => log("Warning: " + message);
    }
}
