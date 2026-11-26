using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;

namespace HCG.ExecutableMaker.Packaging.Windows;

/// <summary>
/// The Release Passport build half for a Windows file (installer Setup.exe, portable ZIP). It records:
/// <list type="bullet">
/// <item>the exact file (SHA-256, size) and the architecture it installs or runs on;</item>
/// <item>for an installer, its bootstrapper (Inno Setup's x86 loader) separately from the installed program;</item>
/// <item>the installed program's PE machine type and version resource;</item>
/// <item><c>code_signing</c> from Authenticode, for the file and each program, read from the bytes;</item>
/// <item>the source commit as Git states it, and the toolchain;</item>
/// <item>the artifact's verification checks.</item>
/// </list>
/// Repository signing, channel, access, approvals and publication are not the builder's to state.
/// </summary>
public static class WindowsPassport
{
    public const string CodeSigningScope = "application code inside the file (Windows PE, Authenticode); independent of repository signing";

    public sealed record Facts(
        ArtifactRecord Artifact, string ProductName, string Package, string Version, string? BuildNumber, string Architecture,
        SourceProvenance Source, string Provider, string? Toolchain, DateTimeOffset BuiltAt,
        PeFile App, string AppRelativePath, PeFile? Bootstrapper, AuthenticodeReport? FileSignature, IReadOnlyList<AuthenticodeReport> ProgramSignatures,
        string? ProductKey = null, string? Edition = null, string IssuedBy = "HCG Executable Maker");

    public static string KindOf(PackagingFormat f) => f switch
    {
        PackagingFormat.WindowsInstallerExe => "installer",
        PackagingFormat.PortableZip => "portable zip",
        _ => f.DisplayName(),
    };

    public static JsonObject CodeSigning(AuthenticodeReport? file, IReadOnlyList<AuthenticodeReport> programs)
    {
        var all = (file is null ? programs : programs.Prepend(file)).ToList();
        static JsonObject Entry(AuthenticodeReport r) => new()
        {
            ["path"] = Path.GetFileName(r.Path) == r.Path ? r.Path : r.Path.Replace('\\', '/'),
            ["machine"] = r.Machine, ["status"] = r.Status, ["signer"] = r.Signer, ["issuer"] = r.Issuer, ["thumbprint"] = r.Thumbprint,
            ["digest_algorithm"] = r.DigestAlgorithm, ["digest_matches"] = r.DigestMatches, ["chain_trusted"] = r.ChainTrusted,
            ["timestamp"] = r.Timestamp is { } t ? new JsonObject { ["kind"] = t.Kind, ["time"] = t.Time?.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["authority"] = t.Authority, ["verified"] = t.Verified } : null,
            ["windows_verdict"] = r.WindowsVerdict,
        };
        var signed = all.Count(r => r.Signed);
        return new JsonObject
        {
            ["scope"] = CodeSigningScope,
            ["method_claimed"] = signed > 0 ? "authenticode" : null,
            ["binaries"] = all.Count,
            ["all_signed"] = all.Count == 0 ? null : all.All(r => r.Status == "signed"),
            ["entitlements"] = new JsonObject(),
            ["status"] = all.Any(r => r.Status == "invalid") ? "invalid" : signed == 0 ? "unsigned" : all.All(r => r.Status == "signed") ? "signed" : all.Any(r => r.Status == "unsigned") ? "partly-signed" : "signed-untrusted",
            ["verified_by"] = "HCG Executable Maker (PE image digest, signer signature, timestamp and chain, read from the file)",
            ["file"] = file is null ? null : Entry(file),
            ["programs"] = new JsonArray(programs.Select(p => (JsonNode?)Entry(p)).ToArray()),
        };
    }

    public static JsonObject ForArtifact(Facts f)
    {
        var a = f.Artifact;
        var failed = a.Checks.Any(c => c.Status == VerificationStatus.Failed);
        var warned = a.Checks.Any(c => c.Status is VerificationStatus.PassedWithWarnings or VerificationStatus.NotVerifiable);
        var installer = a.Format == PackagingFormat.WindowsInstallerExe;
        var note = installer
            ? $"{a.Checks.Count} checks of the installer by HCG Executable Maker. Bootstrapper: {f.Bootstrapper?.MachineName ?? "?"} Inno Setup loader, installs for {f.Architecture} only. " +
              $"Installed program {f.AppRelativePath}: {f.App.MachineName}{(f.App.ProductVersion is { } pv ? $", product version {pv}" : "")}."
            : $"{a.Checks.Count} checks of the portable ZIP by HCG Executable Maker. Program {f.AppRelativePath}: {f.App.MachineName}{(f.App.ProductVersion is { } pv2 ? $", product version {pv2}" : "")}.";
        var passport = ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: "windows",
            Architecture: f.Architecture,
            Package: f.Package,
            Version: f.Version,
            BuildNumber: f.BuildNumber,
            ProductName: f.ProductName,
            FileName: a.FileName,
            Sha256: a.Sha256,
            Size: a.SizeBytes,
            ArtifactKind: KindOf(a.Format),
            Source: f.Source,
            Provider: f.Provider,
            Toolchain: f.Toolchain,
            BuiltAt: f.BuiltAt,
            StaticInspectionState: failed ? "failed" : warned ? "passed-with-warnings" : "passed",
            StaticInspectionNote: note,
            Checks: a.Checks.Select(c => (c.Name, c.Status.ToString(), c.Detail)).ToList(),
            Tests: null,
            CodeSigning: CodeSigning(f.FileSignature, f.ProgramSignatures),
            IssuedBy: f.IssuedBy));
        if (f.ProductKey is not null) passport["product"]!["key"] = f.ProductKey;
        passport["edition"] = f.Edition;
        passport["windows"] = new JsonObject
        {
            ["installed_program"] = new JsonObject
            {
                ["path"] = f.AppRelativePath, ["machine"] = f.App.MachineName, ["subsystem"] = f.App.IsGui ? "gui" : "console",
                ["product_version"] = f.App.ProductVersion, ["file_version"] = f.App.FileVersion,
                ["company"] = f.App.Version.GetValueOrDefault("CompanyName"), ["product"] = f.App.Version.GetValueOrDefault("ProductName"),
            },
            ["bootstrapper"] = f.Bootstrapper is { } b
                ? new JsonObject { ["machine"] = b.MachineName, ["kind"] = "Inno Setup loader", ["note"] = "the setup program's own architecture; not the installed program's" }
                : null,
        };
        return passport;
    }
}
