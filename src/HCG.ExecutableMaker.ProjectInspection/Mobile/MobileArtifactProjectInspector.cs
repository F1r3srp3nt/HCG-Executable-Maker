using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Recognizes an imported mobile artifact (a single .apk/.aab/.apks/.ipa). Artifacts are inspected and verified, never
/// "converted" into another platform's format; the one transformation offered is bundletool's AAB → APK set.
/// </summary>
public sealed class MobileArtifactProjectInspector(TimeProvider time) : IProjectInspector
{
    public string Id => "mobile-artifact";
    public string DisplayName => "Mobile artifact";

    private static readonly string[] Extensions = [".apk", ".aab", ".apks", ".ipa"];

    private static string? Artifact(SourceIndex index) =>
        index.Files.Count == 1 && !index.Files[0].Contains('/') && Extensions.Any(e => index.Files[0].EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ? index.Files[0] : null;

    public int Match(SourceIndex index) => Artifact(index) is null ? 0 : 120;

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken)
    {
        var file = Artifact(index) ?? throw new InvalidOperationException("Not a mobile artifact workspace.");
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var ios = ext == ".ipa";
        var mobile = new MobileAnalysis
        {
            Framework = ios ? MobileFramework.NativeIos : MobileFramework.NativeAndroid,
            Platforms = [ios ? TargetPlatform.IOS : TargetPlatform.Android],
            ArtifactPath = file,
        };
        var (type, summary) = ext switch
        {
            ".aab" => ("Android App Bundle (artifact)", "Imported App Bundle. Mobile Build Studio verifies it and can generate APKs from it with bundletool."),
            ".apks" => ("Android APK set (artifact)", "Imported APK set. Mobile Build Studio verifies its contents."),
            ".ipa" => ("iOS app package (artifact)", "Imported IPA. Mobile Build Studio verifies its structure, metadata and signing state."),
            _ => ("Android APK (artifact)", "Imported APK. Mobile Build Studio verifies its manifest, ABIs and signing state. An APK cannot be turned into an App Bundle."),
        };
        var options = MobileCapabilities.Evaluate(mobile, toolchains);
        return Task.FromResult(new ProjectAnalysis
        {
            InspectorId = Id, ProjectName = Path.GetFileNameWithoutExtension(file), Language = "—", Framework = type,
            Runtime = ios ? "iOS" : "Android", ApplicationType = type,
            Support = SupportLevel.Supported,
            SupportSummary = summary,
            PrimaryProjectPath = null,
            MissingDependencies = options.Where(o => !o.IsAvailable && o.Reason is not null).Select(o => o.Reason!).ToList(),
            Mobile = mobile,
            AnalyzedAt = time.GetUtcNow(),
        });
    }
}
