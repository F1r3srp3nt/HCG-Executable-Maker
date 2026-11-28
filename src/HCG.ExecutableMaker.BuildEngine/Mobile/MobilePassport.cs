using System.IO.Compression;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// The Release Passport build half for a device IPA from Mobile Build Studio. It records what Maker measured on these
/// exact bytes:
/// <list type="bullet">
/// <item>the file's SHA-256 and size;</item>
/// <item>the app's bundle ID, version, build number and architecture from Info.plist and the Mach-O header;</item>
/// <item>the source commit as Git states it, and the toolchain;</item>
/// <item><c>code_signing</c> read from the app inside the IPA: every Mach-O and whether it carries a signature, the main
/// executable's signed entitlements, the embedded profile's name, team and type, and the verdict of Apple's
/// <c>codesign --verify</c> from the artifact's own checks (on a Mac);</item>
/// <item>the artifact checks.</item>
/// </list>
/// Device installation and launch are left "not-recorded": the builder never claims them. Written as
/// <c>&lt;file&gt;.ipa.release-passport.json</c>, where Ship to Store finds it.
/// </summary>
public static class MobilePassport
{
    /// <summary>The passport platform for an iOS app: the App Store kind when its profile is an App Store profile.</summary>
    public static string Platform(MobileArtifactReport r) => r.SigningSchemes.Contains("App Store Connect") ? "ios-app-store" : "ios";

    /// <summary>The app bundle inside an IPA, extracted into <paramref name="scratch"/> (names are checked against traversal).</summary>
    public static string? ExtractApp(string ipa, string scratch)
    {
        Directory.CreateDirectory(scratch);
        var root = Path.GetFullPath(scratch) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(ipa);
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("Payload/", StringComparison.Ordinal) || e.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(scratch, e.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
        var payload = Path.Combine(scratch, "Payload");
        return Directory.Exists(payload) ? Directory.EnumerateDirectories(payload, "*.app").FirstOrDefault() : null;
    }

    /// <summary><c>code_signing</c> of an iOS app as its bytes and its artifact checks state it.</summary>
    public static JsonObject CodeSigning(string app, MobileArtifactReport report, IReadOnlyList<VerificationCheck> checks)
    {
        var machO = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories).Where(MachOLinkage.IsMachO).ToList();
        var slices = machO.ToDictionary(f => f, f => { try { return MachOLinkage.Read(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return []; } });
        var unsigned = slices.Where(s => s.Value.Count == 0 || s.Value.Any(x => !x.HasCodeSignature)).Select(s => Path.GetRelativePath(app, s.Key)).ToList();
        var exe = Path.Combine(app, Path.GetFileNameWithoutExtension(app));
        var info = Path.Combine(app, "Info.plist");
        try
        {
            if (File.Exists(info) && PlistReader.ParseDictionary(File.ReadAllBytes(info)).GetValueOrDefault("CFBundleExecutable") is string name) exe = Path.Combine(app, name);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { }
        var entitlementKeys = new JsonArray();
        var xml = slices.GetValueOrDefault(exe)?.Select(s => s.EntitlementsXml).FirstOrDefault(x => x is not null);
        if (xml is not null)
        {
            try
            {
                foreach (var k in PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(xml)).Keys.Order(StringComparer.Ordinal)) entitlementKeys.Add((JsonNode?)k);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { }
        }
        var profilePath = Path.Combine(app, "embedded.mobileprovision");
        var profile = MobileArtifactInspector.ParseProvisioningProfile(File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : null);
        var codesign = checks.FirstOrDefault(c => c.Name == "codesign --verify");
        return new JsonObject
        {
            ["scope"] = ReleasePassport.CodeSigningScope,
            ["method_claimed"] = report.Signing switch
            {
                MobileSigningState.DevelopmentSigned => "apple-development",
                MobileSigningState.DistributionSigned => "apple-distribution",
                MobileSigningState.AdHocSigned => "ad-hoc",
                _ => "unsigned",
            },
            ["binaries"] = machO.Count,
            ["all_signed"] = machO.Count > 0 && unsigned.Count == 0,
            ["entitlements"] = new JsonObject { [Path.GetFileName(app)] = entitlementKeys },
            ["profile"] = profile is null ? null : new JsonObject
            {
                ["name"] = profile.Name, ["team_id"] = profile.TeamId, ["team_name"] = profile.TeamName,
                ["type"] = report.SigningSchemes.FirstOrDefault(), ["expires"] = profile.Expiration?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["get_task_allow"] = profile.GetTaskAllow,
            },
            ["codesign_verify"] = codesign is null
                ? "not run: Apple's codesign verifies signatures only on a Mac"
                : $"{(codesign.Status == VerificationStatus.Passed ? "passed" : "failed")}: {codesign.Detail}",
            ["unsigned_binaries"] = new JsonArray(unsigned.Select(u => (JsonNode?)u).ToArray()),
        };
    }

    public static JsonObject ForIpa(ArtifactRecord artifact, MobileArtifactReport report, string app, IReadOnlyList<VerificationCheck> checks,
        SourceProvenance source, string? toolchain, DateTimeOffset builtAt)
    {
        var failed = checks.Any(c => c.Status == VerificationStatus.Failed);
        var warned = checks.Any(c => c.Status == VerificationStatus.PassedWithWarnings);
        return ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: Platform(report),
            Architecture: report.Architectures.Count == 1 ? report.Architectures[0] : report.Architectures.Count == 0 ? "unknown" : string.Join("+", report.Architectures),
            Package: report.PackageId ?? Path.GetFileNameWithoutExtension(artifact.FileName),
            Version: report.VersionName ?? "0",
            BuildNumber: report.VersionCode,
            ProductName: Path.GetFileNameWithoutExtension(app),
            FileName: artifact.FileName,
            Sha256: artifact.Sha256,
            Size: artifact.SizeBytes,
            ArtifactKind: "iOS app package (.ipa, " + (report.SigningSchemes.FirstOrDefault() ?? report.Signing.ToString()) + ")",
            Source: source,
            Provider: "executable-maker-local",
            Toolchain: toolchain,
            BuiltAt: builtAt,
            StaticInspectionState: failed ? "failed" : warned ? "passed-with-warnings" : "passed",
            StaticInspectionNote: $"{checks.Count} checks of the IPA by Executable Maker (structure, Info.plist, platform, embedded profile, signature{(checks.Any(c => c.Name == "codesign --verify") ? ", Apple's codesign --verify" : "")})",
            Checks: checks.Select(c => (c.Name, c.Status.ToString(), c.Detail)).ToList(),
            Tests: null,
            CodeSigning: CodeSigning(app, report, checks),
            IssuedBy: "HCG Executable Maker " + HCG.ExecutableMaker.Core.HostInfo.ProductVersion));
    }
}
