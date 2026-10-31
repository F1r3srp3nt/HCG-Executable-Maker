namespace HCG.ExecutableMaker.Domain;

public enum ToolchainStatus
{
    Installed,
    Missing,
    UnsupportedOnHost,
    ValidationFailed,
}

public sealed record ToolchainInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required ToolchainStatus Status { get; init; }
    public string? Version { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Detail { get; init; }
    /// <summary>How to obtain the tool when missing. HCG never installs tools automatically.</summary>
    public string? InstallHint { get; init; }
    public IReadOnlyList<string> AdditionalVersions { get; init; } = [];

    public bool IsAvailable => Status == ToolchainStatus.Installed;
}

public sealed record ToolchainReport(
    HostEnvironment Host,
    IReadOnlyList<ToolchainInfo> Toolchains,
    long? AvailableDiskBytes,
    string? DiskPath,
    DateTimeOffset DetectedAt)
{
    public ToolchainInfo? Find(string id) => Toolchains.FirstOrDefault(t => t.Id == id);
    public bool IsAvailable(string id) => Find(id)?.IsAvailable == true;
}

public static class ToolchainIds
{
    public const string DotNetSdk = "dotnet-sdk";
    public const string Git = "git";
    public const string XcodeCommandLineTools = "xcode-clt";
    public const string Xcode = "xcode";
    public const string Codesign = "codesign";
    public const string Hdiutil = "hdiutil";
    public const string Notarytool = "notarytool";
    public const string Productbuild = "productbuild";
    public const string WindowsSdk = "windows-sdk";
    public const string SignTool = "signtool";
    public const string MsBuild = "msbuild";
    public const string MakeAppx = "makeappx";
    public const string WixToolset = "wix";
    public const string InnoSetup = "inno-setup";
}
