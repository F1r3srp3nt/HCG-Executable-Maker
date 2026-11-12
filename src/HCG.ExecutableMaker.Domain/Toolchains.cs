namespace HCG.ExecutableMaker.Domain;

public enum ToolchainStatus
{
    /// <summary>Ready: found AND validated (a version probe or structure check passed), not merely present.</summary>
    Installed,
    /// <summary>Not found anywhere HCG looks.</summary>
    Missing,
    /// <summary>Belongs to another host OS (e.g. Windows SDK on a Mac). Not a fault of this machine.</summary>
    UnsupportedOnHost,
    /// <summary>Found, but the probe failed for another reason (see Detail for the tool's own output).</summary>
    ValidationFailed,
    /// <summary>A saved or configured path does not contain the expected tool (e.g. a folder that is not a JDK).</summary>
    InvalidPath,
    /// <summary>Found and working, but the version is too old (or too new) for what HCG needs.</summary>
    IncompatibleVersion,
    /// <summary>Installed but needs a user step first (select Xcode, first launch, licence, SDK component).</summary>
    SetupRequired,
}

/// <summary>Which kind of work a tool serves; drives grouping and whether a missing tool is a problem.</summary>
public enum ToolchainRole
{
    Core,
    MacDesktop,
    Android,
    AppleMobile,
    Flutter,
    Maui,
    Windows,
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
    /// <summary>Where the tool was found: "Saved in HCG", "JAVA_HOME", "Android Studio", "PATH", …</summary>
    public string? Source { get; init; }
    /// <summary>Official setup page for this tool.</summary>
    public string? SetupUrl { get; init; }

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
    /// <summary>Xcode discovery and diagnosis (macOS only).</summary>
    public XcodeStatus? Xcode { get; init; }
    /// <summary>JDK discovery: the selected JDK and every candidate examined (with why it was rejected).</summary>
    public JavaDiscoveryResult? Java { get; init; }
    /// <summary>Android SDK discovery: platforms and build-tools listed separately.</summary>
    public AndroidSdkDiscoveryResult? AndroidSdk { get; init; }
    /// <summary>Valid code-signing identities in the keychain (macOS), classified by certificate type.</summary>
    public IReadOnlyList<SigningIdentity> AppleIdentities { get; init; } = [];
    /// <summary>iOS provisioning profiles (*.mobileprovision) on this Mac.</summary>
    public IReadOnlyList<IosProvisioningProfile> IosProfiles { get; init; } = [];
    /// <summary>Node.js installations found (PATH and the usual install locations), newest first.</summary>
    public IReadOnlyList<NodeInstallation> NodeInstallations { get; init; } = [];
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

    // Mobile
    public const string Jdk = "jdk";
    public const string AndroidSdk = "android-sdk";
    public const string AndroidBuildTools = "android-build-tools";
    public const string AndroidPlatforms = "android-platforms";
    public const string Apksigner = "apksigner";
    public const string Zipalign = "zipalign";
    public const string Aapt2 = "aapt2";
    public const string Bundletool = "bundletool";
    public const string IosSdk = "ios-sdk";
    public const string AppleSigningIdentities = "apple-signing-identities";
    public const string ProvisioningProfiles = "provisioning-profiles";
    public const string Flutter = "flutter";
    public const string MauiWorkloads = "maui-workloads";

    // Swift / macOS desktop
    public const string Swift = "swift";
    public const string MacOSSdk = "macos-sdk";
    public const string MacOSVersion = "macos-version";
    /// <summary>XCTest for the selected developer directory: needed to RUN Swift tests, not to build or package apps.</summary>
    public const string XcTest = "xctest";

    // Node.js / Electron
    public const string Node = "node";
    public const string Npm = "npm";

    // C / C++
    public const string CMake = "cmake";
    public const string Ninja = "ninja";
    public const string Make = "make";
    public const string CCompiler = "c-compiler";
    public const string CxxCompiler = "cxx-compiler";
    /// <summary>Visual Studio with the C++ workload (Windows): CMake's default generator uses it.</summary>
    public const string MsvcTools = "msvc";
    // Theos (jailbreak packages)
    public const string DotNetIosWorkload = "dotnet-ios-workload";
    public const string Theos = "theos";
    public const string TheosMake = "theos-make";
    public const string Ldid = "ldid";
    public const string Perl = "perl";
    public const string TheosIosToolchain = "theos-ios-toolchain";
}
