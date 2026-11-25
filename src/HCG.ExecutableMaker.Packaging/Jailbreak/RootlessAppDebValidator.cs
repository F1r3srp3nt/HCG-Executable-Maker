using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Theos;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>What the package is expected to be. Every field is optional; a check without an expectation reports facts.</summary>
public sealed record RootlessAppExpectation(
    string? PackageId = null,
    string? Version = null,
    string? BundleId = null,
    string? DeviceIosVersion = null,
    IReadOnlyList<(string Version, string Sha256)>? PublishedVersions = null,
    IReadOnlyList<string>? AllowedEntitlements = null);

/// <summary>
/// Validates a rootless jailbreak application package without installing it. Every check is read from the .deb's own
/// bytes: the ar container and gzip members (decompressed completely), a full extraction with entry accounting, the
/// control file and maintainer scripts, install paths, the app's Info.plist, and every Mach-O (architecture, platform,
/// minimum iOS, code signature, encryption, entitlements and every library it loads).
/// </summary>
public static class RootlessAppDebValidator
{
    public sealed record Facts(
        string Sha256, long Size, IReadOnlyDictionary<string, string> Control, string? AppPath, string? BundleId, string? MinimumOS,
        int FileCount, long PayloadBytes, IReadOnlyDictionary<string, string> Scripts, IReadOnlyDictionary<string, string> Entitlements);

    public sealed record Result(Facts Facts, IReadOnlyList<VerificationCheck> Checks, string Report)
    {
        public bool Passed => Checks.All(c => c.Status is VerificationStatus.Passed or VerificationStatus.PassedWithWarnings or VerificationStatus.NotVerifiable);
    }

    private static readonly string[] SystemLibraryPrefixes = ["/usr/lib/", "/System/Library/"];
    private static readonly string[] RootfulJailbreakPrefixes = ["/Library/", "/usr/lib/TweakInject", "/usr/lib/libsubstrate", "/usr/lib/libhooker", "/usr/lib/libsubstitute", "/usr/local/"];

    public static async Task<Result> ValidateAsync(string debPath, RootlessAppExpectation expect, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var report = new StringBuilder();
        var bytes = await File.ReadAllBytesAsync(debPath, ct).ConfigureAwait(false);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        report.AppendLine($"{Path.GetFileName(debPath)} · {bytes.Length:N0} bytes · SHA-256 {sha}");
        checks.Add(new("Checksum", VerificationStatus.Passed, $"SHA-256 {sha}, {bytes.Length:N0} bytes"));

        var work = Path.Combine(Path.GetTempPath(), "hcg-debcheck-" + Guid.NewGuid().ToString("N")[..10]);
        var control = new Dictionary<string, string>();
        var scripts = new Dictionary<string, string>();
        var entitlements = new Dictionary<string, string>();
        string? appRel = null, bundleId = null, minOS = null;
        var fileCount = 0;
        long payloadBytes = 0;
        try
        {
            // 1. Container: exactly debian-binary, control.tar.gz, data.tar.gz.
            IReadOnlyList<TheosDebVerifier.ArMember> members;
            using (var ms = new MemoryStream(bytes)) members = TheosDebVerifier.ReadAr(ms);
            var names = members.Select(m => m.Name).ToList();
            var structureOk = names.SequenceEqual(["debian-binary", "control.tar.gz", "data.tar.gz"]);
            var debianBinary = members.FirstOrDefault(m => m.Name == "debian-binary") is { } db ? Encoding.ASCII.GetString(bytes, (int)db.Offset, (int)db.Size) : "";
            checks.Add(new("Debian archive", structureOk && debianBinary == "2.0\n" ? VerificationStatus.Passed : VerificationStatus.Failed,
                structureOk && debianBinary == "2.0\n"
                    ? "Format 2.0 · control.tar.gz · data.tar.gz (gzip, as candidate packages require)"
                    : $"Members: {string.Join(", ", names)}; debian-binary \"{debianBinary.Trim()}\". Candidate packages must be format 2.0 with gzip members (control.tar.gz, data.tar.gz); xz, lzma and zstd members are refused."));
            if (!structureOk) return Finish();

            // 2. gzip integrity (complete decompression) and 3. complete extraction with entry accounting.
            var controlDir = Path.Combine(work, "control");
            var dataDir = Path.Combine(work, "data");
            var controlEntries = await ExtractAllAsync(bytes, members[1], controlDir, ct).ConfigureAwait(false);
            var dataEntries = await ExtractAllAsync(bytes, members[2], dataDir, ct).ConfigureAwait(false);
            var extractionProblems = controlEntries.Problems.Concat(dataEntries.Problems).ToList();
            checks.Add(new("gzip members", controlEntries.GzipOk && dataEntries.GzipOk ? VerificationStatus.Passed : VerificationStatus.Failed,
                controlEntries.GzipOk && dataEntries.GzipOk
                    ? $"Both decompress completely: control.tar {controlEntries.TarBytes:N0} bytes, data.tar {dataEntries.TarBytes:N0} bytes"
                    : "A gzip member is corrupt or truncated: " + string.Join("; ", extractionProblems)));
            fileCount = dataEntries.Files;
            payloadBytes = dataEntries.FileBytes;
            checks.Add(new("Complete extraction", extractionProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                extractionProblems.Count == 0
                    ? $"Every entry extracted and its size confirmed: {dataEntries.Files} files, {dataEntries.Directories} folders, {dataEntries.Links} symbolic links, {dataEntries.FileBytes:N0} bytes"
                    : string.Join("; ", extractionProblems)));
            if (!controlEntries.GzipOk || !dataEntries.GzipOk) return Finish();

            // 4. Control metadata.
            var controlFile = Path.Combine(controlDir, "control");
            if (File.Exists(controlFile)) foreach (var (k, v) in TheosDebVerifier.ParseControl(await File.ReadAllTextAsync(controlFile, ct).ConfigureAwait(false))) control[k] = v;
            report.AppendLine().AppendLine("control:");
            foreach (var (k, v) in control) report.AppendLine($"  {k}: {v}");
            var meta = new List<string>();
            foreach (var f in new[] { "Package", "Version", "Architecture", "Maintainer", "Description", "Name" })
                if (!control.ContainsKey(f)) meta.Add($"no {f} field");
            var pkg = control.GetValueOrDefault("Package");
            var ver = control.GetValueOrDefault("Version");
            if (pkg is not null && !RootlessAppPackager.PackageIdRegex().IsMatch(pkg)) meta.Add($"Package \"{pkg}\" is not a valid Debian name");
            if (ver is not null && !RootlessAppPackager.VersionRegex().IsMatch(ver)) meta.Add($"Version \"{ver}\" is not a valid Debian version");
            if (expect.PackageId is { } wantPkg && pkg != wantPkg) meta.Add($"Package is {pkg}, expected {wantPkg}");
            if (expect.Version is { } wantVer && ver != wantVer) meta.Add($"Version is {ver}, expected {wantVer}");
            checks.Add(new("Package metadata", meta.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                meta.Count == 0 ? $"{pkg} {ver} · {control.GetValueOrDefault("Name")} · Maintainer {control.GetValueOrDefault("Maintainer")}" : string.Join("; ", meta)));
            var arch = control.GetValueOrDefault("Architecture");
            checks.Add(new("Rootless architecture", arch == RootlessAppPackager.Architecture ? VerificationStatus.Passed : VerificationStatus.Failed,
                arch == RootlessAppPackager.Architecture ? "iphoneos-arm64 (rootless: Dopamine, palera1n rootless)" : $"Architecture is {arch ?? "(none)"}; a rootless package must be iphoneos-arm64"));

            // 5. Maintainer scripts: only postinst/postrm, and what they do is shown.
            foreach (var file in Directory.EnumerateFiles(controlDir))
            {
                var name = Path.GetFileName(file);
                if (name is "control" or "md5sums") continue;
                scripts[name] = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            var unexpected = scripts.Keys.Where(k => k is not ("postinst" or "postrm")).ToList();
            foreach (var (name, text) in scripts) report.AppendLine().AppendLine($"{name}:").AppendLine(text);

            // 6. Install paths: everything under ./var/jb, one app under var/jb/Applications.
            var allPaths = dataEntries.Paths;
            var outside = allPaths.Where(p => p is not ("./" or "./var/" or "./var/jb/") && !p.StartsWith("./var/jb/", StringComparison.Ordinal)).ToList();
            var apps = allPaths.Select(p => p.StartsWith("./var/jb/Applications/", StringComparison.Ordinal) ? p["./var/jb/Applications/".Length..].Split('/')[0] : null)
                .Where(n => n is { Length: > 0 } && n.EndsWith(".app", StringComparison.Ordinal)).Distinct().ToList();
            var otherUnderJb = allPaths.Where(p => p != "./var/jb/" && p.StartsWith("./var/jb/", StringComparison.Ordinal) && !p.StartsWith("./var/jb/Applications/", StringComparison.Ordinal)).ToList();
            var pathProblems = new List<string>();
            if (outside.Count > 0) pathProblems.Add($"{outside.Count} entr{(outside.Count == 1 ? "y is" : "ies are")} outside /var/jb (e.g. {outside[0]}); a rootless package installs only under /var/jb");
            if (apps.Count != 1) pathProblems.Add(apps.Count == 0 ? "no app under /var/jb/Applications" : $"{apps.Count} apps under /var/jb/Applications ({string.Join(", ", apps)})");
            checks.Add(new("Rootless install paths", pathProblems.Count == 0 ? (otherUnderJb.Count == 0 ? VerificationStatus.Passed : VerificationStatus.PassedWithWarnings) : VerificationStatus.Failed,
                pathProblems.Count == 0
                    ? $"All {allPaths.Count} entries are under /var/jb; app at /var/jb/Applications/{apps[0]}" + (otherUnderJb.Count == 0 ? "" : $"; also installs {string.Join(", ", otherUnderJb.Take(3))}")
                    : string.Join("; ", pathProblems)));
            if (apps.Count != 1) return Finish();
            appRel = "var/jb/Applications/" + apps[0];
            var appDir = Path.Combine(dataDir, "var", "jb", "Applications", apps[0]);

            // 7. The app: Info.plist identity, executable, minimum iOS.
            var plistFile = Path.Combine(appDir, "Info.plist");
            Dictionary<string, object>? info = null;
            try { if (File.Exists(plistFile)) info = PlistReader.ParseDictionary(await File.ReadAllBytesAsync(plistFile, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException) { checks.Add(new("App bundle", VerificationStatus.Failed, "Info.plist cannot be read: " + ex.Message)); }
            bundleId = info?.GetValueOrDefault("CFBundleIdentifier") as string;
            minOS = info?.GetValueOrDefault("MinimumOSVersion") as string;
            var exe = info?.GetValueOrDefault("CFBundleExecutable") as string;
            var exePath = exe is null ? null : Path.Combine(appDir, exe);
            UnixFileMode? exeMode = exe is not null && dataEntries.Modes.TryGetValue($"./{appRel}/{exe}", out var em) ? em : null;
            var appProblems = new List<string>();
            if (info is null) appProblems.Add("no Info.plist at the top of the app (an iOS app keeps it there)");
            if (bundleId is null) appProblems.Add("no CFBundleIdentifier");
            else if (expect.BundleId is { } wantBundle && bundleId != wantBundle) appProblems.Add($"bundle ID is {bundleId}, expected {wantBundle}");
            if (exePath is null || !File.Exists(exePath)) appProblems.Add($"the executable {exe ?? "(none)"} is missing");
            else if (exeMode is { } m && (m & UnixFileMode.UserExecute) == 0) appProblems.Add($"the executable {exe} is not marked executable (mode {Convert.ToString((int)m, 8)})");
            checks.Add(new("App bundle", appProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                appProblems.Count == 0 ? $"{bundleId} · executable {exe} (0755) · MinimumOSVersion {minOS ?? "(not set)"}" : string.Join("; ", appProblems)));

            // 8. Dependencies.
            var depends = control.GetValueOrDefault("Depends") ?? "";
            var depList = depends.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var depProblems = new List<string>();
            var firmware = depList.FirstOrDefault(d => d.StartsWith("firmware", StringComparison.Ordinal));
            if (minOS is not null && firmware != $"firmware (>= {minOS})") depProblems.Add($"Depends should declare firmware (>= {minOS}) (the app's MinimumOSVersion); it has {firmware ?? "none"}");
            if (!depList.Any(d => d == "uikittools" || d.StartsWith("uikittools ", StringComparison.Ordinal))) depProblems.Add("Depends lacks uikittools (it provides uicache, which registers the app)");
            checks.Add(new("Dependencies", depProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                depProblems.Count == 0 ? $"Depends: {depends}" : string.Join("; ", depProblems)));

            // 9. Registration scripts.
            var installPath = "/" + appRel;
            var scriptProblems = new List<string>();
            if (unexpected.Count > 0) scriptProblems.Add($"unexpected maintainer scripts: {string.Join(", ", unexpected)} (an app package needs only postinst/postrm to register it)");
            if (!scripts.TryGetValue("postinst", out var pi) || !pi.Contains($"uicache -p \"{installPath}\"", StringComparison.Ordinal)) scriptProblems.Add($"postinst does not register {installPath} with uicache -p");
            if (!scripts.TryGetValue("postrm", out var pr) || !pr.Contains($"uicache -u \"{installPath}\"", StringComparison.Ordinal)) scriptProblems.Add($"postrm does not unregister {installPath} with uicache -u");
            checks.Add(new("App registration", scriptProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                scriptProblems.Count == 0 ? $"postinst runs uicache -p {installPath}; postrm runs uicache -u {installPath}; no other scripts" : string.Join("; ", scriptProblems)));

            // 10. Mach-O: architecture, platform, minimum iOS, signature, encryption, libraries, entitlements.
            var payloadFiles = new HashSet<string>(dataEntries.Paths.Where(p => !p.EndsWith('/')).Select(p => "/" + p[2..]), StringComparer.Ordinal);
            var machos = Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories)
                .Where(f => new FileInfo(f).LinkTarget is null && MachOLinkage.IsMachO(f)).Order(StringComparer.Ordinal).ToList();
            var binProblems = new List<string>(); var binWarnings = new List<string>();
            var linkProblems = new List<string>(); var linkWarnings = new List<string>();
            var compatProblems = new List<string>();
            var signProblems = new List<string>();
            // Signature integrity: every slice's CodeDirectories verified against the bytes (see MachOSignatureAudit).
            var integrityProblems = new List<string>(); var integrityWarnings = new List<string>();
            var audits = new List<(string Rel, IReadOnlyList<MachOSignatureAudit.Slice> Slices)>();
            var entProblems = new List<string>();
            var systemLibs = new SortedSet<string>(StringComparer.Ordinal);
            // dyld searches @rpath in the loading image's LC_RPATHs and then in those of the images that loaded it — for
            // every library in an app, ultimately the main executable's (Xcode puts /usr/lib/swift and
            // @executable_path/Frameworks there; an embedded libswift_Concurrency.dylib has none of its own).
            var mainExePath = exe is null ? null : Path.Combine(appDir, exe);
            var mainRpaths = mainExePath is not null && File.Exists(mainExePath) && MachOLinkage.IsMachO(mainExePath)
                ? MachOLinkage.Read(mainExePath).SelectMany(x => x.RPaths).Distinct(StringComparer.Ordinal).ToList() : [];
            report.AppendLine().AppendLine("Mach-O files:");
            foreach (var file in machos)
            {
                var rel = Path.GetRelativePath(appDir, file).Replace('\\', '/');
                var installed = installPath + "/" + rel;
                var slices = MachOLinkage.Read(file);
                report.AppendLine($"  {rel}: " + string.Join("; ", slices.Select(s => $"{s.Architecture} {s.Platform ?? "?"} {s.MinimumOS ?? "?"}{(s.HasCodeSignature ? " signed" : " UNSIGNED")}{(s.Encrypted ? " ENCRYPTED" : "")}")));
                var audit = MachOSignatureAudit.Audit(file, BundleOf(file, appDir));
                audits.Add((rel, audit));
                foreach (var a in audit.Where(a => a.Signer != MachOSignatureAudit.SignerKind.None))
                {
                    report.AppendLine($"    signature ({a.Architecture}): {a.SignerText} · " + string.Join(" + ", a.CodeDirectories.Select(c =>
                        $"{c.HashType} {(c.Verifiable ? $"{c.CodeSlots - c.BadPages}/{c.CodeSlots} pages verified" : "no page hashes")}")) +
                        (a.CodeDirectories.FirstOrDefault() is { } cd0 ? $" · identifier {cd0.Identifier} · cdhash {cd0.CdHash}" : "") +
                        (a.CodeDirectories.SelectMany(c => c.SpecialSlots).DistinctBy(kv => kv.Key).ToList() is { Count: > 0 } sp ? " · " + string.Join(", ", sp.Select(kv => $"{kv.Key} {kv.Value}")) : ""));
                    if (a.Problem is { } p) integrityProblems.Add($"{rel} ({a.Architecture}): {p}");
                    if (a.BadPages > 0)
                        integrityProblems.Add($"{rel} ({a.Architecture}): {a.BadPages} of {a.CodeDirectories.Sum(c => c.CodeSlots)} page hashes do not match the file — it was changed after it was signed, and iOS kills the process when it loads those pages");
                    foreach (var (slot, state) in a.CodeDirectories.SelectMany(c => c.SpecialSlots).Where(kv => kv.Value != "matches").DistinctBy(kv => kv.Key))
                        if (slot is "Entitlements" or "DER entitlements" or "Requirements") integrityProblems.Add($"{rel} ({a.Architecture}): the signature's {slot} hash {state.ToLowerInvariant()} (the signature is internally inconsistent)");
                        else integrityWarnings.Add($"{rel} ({a.Architecture}): the {slot} it was signed with {(state.StartsWith("bound", StringComparison.Ordinal) ? "is missing" : "has changed")} (a bundle seal; iOS does not check it for code a jailbreak trusts, but the files differ from what was signed)");
                    if (a.Signer == MachOSignatureAudit.SignerKind.Certificate)
                        integrityWarnings.Add($"{rel} ({a.Architecture}): {a.SignerText}; kept as built (a jailbreak trusts its cdhash like any ad hoc signature)");
                }
                if (slices.Count == 0) { binProblems.Add($"{rel}: unreadable Mach-O"); continue; }
                if (!slices.Any(s => s.Architecture is "arm64" or "arm64e")) binProblems.Add($"{rel}: no arm64 slice ({string.Join(", ", slices.Select(s => s.Architecture))})");
                foreach (var s in slices)
                {
                    if (s.Platform == "iOS Simulator") binProblems.Add($"{rel}: built for the iOS Simulator");
                    else if (s.Platform is not null and not "iOS") binProblems.Add($"{rel}: built for {s.Platform}");
                    else if (s.Platform is null) binWarnings.Add($"{rel} ({s.Architecture}): no platform recorded");
                    if (s.Encrypted) binProblems.Add($"{rel}: encrypted (App Store binary)");
                    if (!s.HasCodeSignature) signProblems.Add($"{rel} ({s.Architecture})");
                    if (expect.DeviceIosVersion is { } dev && s.MinimumOS is { } mos && CompareOs(mos, dev) > 0)
                        compatProblems.Add($"{rel} ({s.Architecture}) needs iOS {mos}");
                    if (s.EntitlementsXml is { Length: > 0 } ex && !entitlements.ContainsKey(rel))
                    {
                        entitlements[rel] = ex;
                        try
                        {
                            var keys = PlistReader.ParseDictionary(Encoding.UTF8.GetBytes(ex)).Keys.ToList();
                            var privileged = keys.Where(k => (RootlessAppPackager.PrivilegedEntitlementKeys.Contains(k) || k.StartsWith("com.apple.private.", StringComparison.Ordinal))
                                                             && !(expect.AllowedEntitlements ?? []).Contains(k)).ToList();
                            if (privileged.Count > 0) entProblems.Add($"{rel}: {string.Join(", ", privileged)}");
                            report.AppendLine($"    entitlements: {string.Join(", ", keys)}");
                        }
                        catch (Exception e) when (e is FormatException or InvalidDataException or InvalidCastException) { entProblems.Add($"{rel}: entitlements cannot be read ({e.Message})"); }
                    }
                    foreach (var d in s.Dylibs)
                    {
                        var weak = d.Kind == MachOLinkage.DylibKind.Weak;
                        if (SystemLibraryPrefixes.Any(p => d.Path.StartsWith(p, StringComparison.Ordinal))) { systemLibs.Add(d.Path); continue; }
                        if (d.Path.StartsWith('@'))
                        {
                            var resolved = Resolve(d.Path, installed, s.RPaths, installPath + "/" + exe)
                                .Concat(Resolve(d.Path, installPath + "/" + exe, mainRpaths, installPath + "/" + exe)).Distinct(StringComparer.Ordinal).ToList();
                            if (resolved.Any(payloadFiles.Contains)) continue;
                            // An rpath into the OS (Xcode adds /usr/lib/swift for Swift apps): the library comes from iOS itself.
                            if (resolved.FirstOrDefault(r => SystemLibraryPrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal))) is { } sys) { systemLibs.Add(sys); continue; }
                            (weak ? linkWarnings : linkProblems).Add($"{rel} loads {d.Path}, which is not in the package{(weak ? " (weak: optional)" : "")}");
                            continue;
                        }
                        if (d.Path.StartsWith("/var/jb/", StringComparison.Ordinal))
                        {
                            if (payloadFiles.Contains(d.Path)) linkWarnings.Add($"{rel} hard-codes {d.Path} (works only when /var/jb is the jailbreak root; libroot resolves it at run time)");
                            else linkProblems.Add($"{rel} loads {d.Path}, which this package does not install (declare the package that provides it in Depends)");
                            continue;
                        }
                        if (RootfulJailbreakPrefixes.Any(p => d.Path.StartsWith(p, StringComparison.Ordinal)))
                        {
                            linkProblems.Add($"{rel} loads {d.Path}: a rootful jailbreak location that does not exist on a rootless device");
                            continue;
                        }
                        (weak ? linkWarnings : linkProblems).Add($"{rel} loads {d.Path}, which is neither a system library nor in the package");
                    }
                }
            }
            checks.Add(new("Binaries", binProblems.Count > 0 ? VerificationStatus.Failed : binWarnings.Count > 0 ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
                binProblems.Count > 0 ? string.Join("; ", binProblems) : $"{machos.Count} Mach-O file(s), device builds for iOS with an arm64 slice" + (binWarnings.Count > 0 ? "; " + string.Join("; ", binWarnings) : "")));
            checks.Add(new("Dynamic libraries", linkProblems.Count > 0 ? VerificationStatus.Failed : linkWarnings.Count > 0 ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
                linkProblems.Count > 0 ? string.Join("; ", linkProblems)
                    : $"Every library resolves: {systemLibs.Count} system librar{(systemLibs.Count == 1 ? "y" : "ies")} (/usr/lib, /System/Library) and the bundle's own frameworks" + (linkWarnings.Count > 0 ? "; " + string.Join("; ", linkWarnings) : "")));
            checks.Add(new("Code signatures", signProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                signProblems.Count == 0 ? "Every Mach-O carries a code signature (an ldid pseudo-signature is enough on a jailbroken device; it is not an Apple signature)"
                    : "Unsigned: " + string.Join(", ", signProblems) + ". A jailbroken device refuses unsigned code; sign with ldid."));
            // Library validation / hardened runtime in the main executable refuses libraries signed by anyone else (dyld).
            if (exe is not null && audits.FirstOrDefault(x => x.Rel == exe).Slices is { } mainSlices && mainSlices.FirstOrDefault(m => m.HardenedRuntime || m.LibraryValidation) is { } strict)
                foreach (var (rel, sl) in audits.Where(x => x.Rel != exe))
                    if (sl.Any(n => n.Signer is MachOSignatureAudit.SignerKind.AdHoc || n.TeamId != strict.TeamId))
                        integrityProblems.Add($"{exe} enforces {(strict.LibraryValidation ? "library validation" : "the hardened runtime")}, and {rel} is {sl[0].SignerText}: dyld refuses to load it");
            var verifiedCount = audits.SelectMany(a => a.Slices).Count(a => a.Verified);
            var unverifiable = audits.SelectMany(a => a.Slices).Count(a => a.Signer != MachOSignatureAudit.SignerKind.None && !a.Verified && a.BadPages == 0 && a.Problem is null);
            checks.Add(new("Signature integrity", integrityProblems.Count > 0 ? VerificationStatus.Failed
                    : verifiedCount == 0 && unverifiable > 0 ? VerificationStatus.NotVerifiable
                    : integrityWarnings.Count > 0 ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
                integrityProblems.Count > 0 ? string.Join("; ", integrityProblems)
                    : (verifiedCount > 0 ? $"{verifiedCount} signature(s) verified page by page against the files: " + string.Join(", ", audits.Select(a => $"{a.Rel} ({string.Join("/", a.Slices.Select(x => x.SignerText).Distinct())})")) : "No signature carries page hashes to verify")
                      + (unverifiable > 0 && verifiedCount > 0 ? $"; {unverifiable} without page hashes" : "")
                      + (integrityWarnings.Count > 0 ? "; " + string.Join("; ", integrityWarnings) : "")));
            checks.Add(new("Entitlements", entProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                entProblems.Count == 0
                    ? (entitlements.Count == 0 ? "No entitlements embedded" : $"Embedded in {entitlements.Count} file(s), none privileged (listed in the report for review)")
                    : "Privileged entitlements need a separate, reviewed decision: " + string.Join("; ", entProblems)));
            if (expect.DeviceIosVersion is { } device)
                checks.Add(new($"Minimum iOS vs iOS {device}", compatProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                    compatProblems.Count == 0
                        ? $"Every binary declares a minimum of iOS {(minOS ?? "?")} or lower, at or below iOS {device}. This is the binaries' declared minimum, not a device test."
                        : $"Will not load on iOS {device}: {string.Join("; ", compatProblems)}. Changing the number alone does not fix this; the code must build for the older iOS."));

            // 11. Versions: published versions are immutable, and a new build needs a higher version.
            if (expect.PublishedVersions is { Count: > 0 } published && ver is not null)
            {
                var same = published.Where(p => p.Version == ver).ToList();
                var higher = published.Where(p => DebianVersion.Compare(p.Version, ver) >= 0 && p.Version != ver).Select(p => p.Version).ToList();
                checks.Add(same.Any(p => !string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    ? new("Version", VerificationStatus.Failed, $"{ver} is already published with different bytes (SHA-256 {same[0].Sha256[..12]}…). Changed bytes need a new version.")
                    : same.Count > 0
                        ? new("Version", VerificationStatus.Passed, $"{ver} is already published with these exact bytes")
                        : higher.Count > 0
                            ? new("Version", VerificationStatus.Failed, $"{ver} sorts below already published {string.Join(", ", higher)}; package managers would not offer it as an upgrade")
                            : new("Version", VerificationStatus.Passed, $"{ver} is new and sorts above every published version ({string.Join(", ", published.Select(p => p.Version))})"));
            }

            // 12. Upgrade and removal (what dpkg will do; not a device test).
            var conffiles = File.Exists(Path.Combine(controlDir, "conffiles"));
            checks.Add(new("Upgrade and removal", conffiles ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
                (conffiles ? "Declares conffiles, which dpkg keeps on removal. " : "No conffiles: ")
                + $"every installed file belongs to {installPath}, so dpkg replaces them on upgrade and removes them all on removal. The app's data container in /var/mobile/Containers is the app's own and is not removed by dpkg."));
            checks.Add(new("Installation on a device", VerificationStatus.NotVerifiable, "Not installed: HCG never installs packages or connects to a device. Device results are recorded only from a real test."));
            return Finish();
        }
        catch (InvalidDataException ex)
        {
            checks.Add(new("Debian archive", VerificationStatus.Failed, ex.Message));
            return Finish();
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        Result Finish()
        {
            report.AppendLine().AppendLine("Checks:");
            foreach (var c in checks) report.AppendLine($"  [{c.Status}] {c.Name}: {c.Detail}");
            return new Result(new Facts(sha, bytes.Length, control, appRel is null ? null : "/" + appRel, bundleId, minOS, fileCount, payloadBytes, scripts, entitlements), checks, report.ToString());
        }
    }

    /// <summary>Where dyld looks for an @-path: @executable_path, @loader_path, and each LC_RPATH entry for @rpath.</summary>
    internal static IEnumerable<string> Resolve(string path, string loaderInstalledPath, IReadOnlyList<string> rpaths, string mainExecutableInstalledPath)
    {
        string Expand(string p) => p
            .Replace("@executable_path", Path.GetDirectoryName(mainExecutableInstalledPath)!.Replace('\\', '/'), StringComparison.Ordinal)
            .Replace("@loader_path", Path.GetDirectoryName(loaderInstalledPath)!.Replace('\\', '/'), StringComparison.Ordinal);
        static string Normalize(string p)
        {
            var parts = new List<string>();
            foreach (var seg in p.Split('/'))
            {
                if (seg is "" or ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return "/" + string.Join('/', parts);
        }
        if (path.StartsWith("@rpath/", StringComparison.Ordinal))
            return rpaths.Select(r => Normalize(Expand(r) + path["@rpath".Length..])).ToList();
        return [Normalize(Expand(path))];
    }

    /// <summary>The bundle a Mach-O belongs to (its .framework, .appex or .app folder): the Info.plist and CodeResources its signature binds.</summary>
    internal static string BundleOf(string file, string appDir)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length >= appDir.Length; dir = Path.GetDirectoryName(dir))
            if (dir.EndsWith(".framework", StringComparison.Ordinal) || dir.EndsWith(".appex", StringComparison.Ordinal) || dir.EndsWith(".app", StringComparison.Ordinal)) return dir;
        return appDir;
    }

    internal static int CompareOs(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var c = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    private sealed class Extraction
    {
        public bool GzipOk = true;
        public long TarBytes;
        public int Files, Directories, Links;
        public long FileBytes;
        public List<string> Problems { get; } = [];
        public List<string> Paths { get; } = [];
        public Dictionary<string, UnixFileMode> Modes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Decompresses a gzip member completely (any corruption fails), then extracts every tar entry, confirming each
    /// file's size. Entries must stay inside the folder; hard links and device files are refused.
    /// </summary>
    private static async Task<Extraction> ExtractAllAsync(byte[] deb, TheosDebVerifier.ArMember member, string targetDir, CancellationToken ct)
    {
        var x = new Extraction();
        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        byte[] tar;
        try
        {
            using var gz = new GZipStream(new MemoryStream(deb, (int)member.Offset, (int)member.Size), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            await gz.CopyToAsync(ms, ct).ConfigureAwait(false);
            tar = ms.ToArray();
            x.TarBytes = tar.Length;
        }
        catch (InvalidDataException ex) { x.GzipOk = false; x.Problems.Add($"{member.Name}: {ex.Message}"); return x; }
        try
        {
            using var reader = new TarReader(new MemoryStream(tar));
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } e)
            {
                var name = e.Name.StartsWith("./", StringComparison.Ordinal) ? e.Name : "./" + e.Name.TrimStart('/');
                x.Paths.Add(name);
                x.Modes[name.TrimEnd('/')] = e.Mode;
                if (name == "./") { x.Directories++; continue; }
                var dest = Path.GetFullPath(Path.Combine(targetDir, name[2..]));
                if (!dest.StartsWith(root, StringComparison.Ordinal) && dest + Path.DirectorySeparatorChar != root) { x.Problems.Add($"{name} points outside the package"); continue; }
                switch (e.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(dest); x.Directories++; break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await using (var fs = File.Create(dest))
                            if (e.DataStream is { } data) await data.CopyToAsync(fs, ct).ConfigureAwait(false);
                        var written = new FileInfo(dest).Length;
                        if (written != e.Length) x.Problems.Add($"{name}: {written} bytes extracted, the archive says {e.Length}");
                        x.Files++; x.FileBytes += written;
                        break;
                    case TarEntryType.SymbolicLink:
                        x.Links++;
                        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dest)!, e.LinkName));
                        if (e.LinkName.StartsWith('/') || !target.StartsWith(root, StringComparison.Ordinal)) x.Problems.Add($"{name} is a link to {e.LinkName}, outside the app");
                        break;
                    default:
                        x.Problems.Add($"{name}: {e.EntryType} entries are not allowed in an app package"); break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException) { x.Problems.Add($"{member.Name}: the tar archive is damaged ({ex.Message})"); }
        return x;
    }
}
