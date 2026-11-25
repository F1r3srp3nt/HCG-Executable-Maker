using HCG.ExecutableMaker.Domain;

namespace HCG.Impactor.Model;

/// <summary>What kind of code bundle sits at a path inside the app.</summary>
public enum BundleKind
{
    /// <summary>The main application (Payload/X.app).</summary>
    App,
    /// <summary>An app extension (.appex in PlugIns/ or Extensions/).</summary>
    AppExtension,
    /// <summary>A watchOS app (Watch/X.app).</summary>
    WatchApp,
    /// <summary>An App Clip (AppClips/X.app).</summary>
    AppClip,
    /// <summary>A framework (.framework).</summary>
    Framework,
    /// <summary>A loose dynamic library (.dylib) — signed on its own, without entitlements.</summary>
    Dylib,
    /// <summary>Any other bundle with an executable (e.g. an XPC service).</summary>
    Other,
}

/// <summary>One CPU slice of a Mach-O binary.</summary>
public sealed record BinarySlice(string Architecture, string? Platform, string? MinimumOS, bool Encrypted, bool HasCodeSignature);

/// <summary>What the embedded code signature of a binary says. Read from the binary, never from file names.</summary>
public sealed record CodeSignatureInfo
{
    public bool Present { get; init; }
    /// <summary>Signed without a certificate (ad hoc; "codesign -s -"). Not installable on a device.</summary>
    public bool AdHoc { get; init; }
    /// <summary>The signing identifier recorded in the CodeDirectory (usually the bundle identifier).</summary>
    public string? Identifier { get; init; }
    public string? TeamId { get; init; }
    /// <summary>"SHA-256", "SHA-1" …</summary>
    public string? HashType { get; init; }
    /// <summary>The leaf certificate that signed the code (from the CMS signature), when one is present.</summary>
    public AppleCertificateInfo? Signer { get; init; }
    /// <summary>The whole certificate chain carried by the signature (leaf first), as common names.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [];
    /// <summary>The signing time recorded in the CMS signature (not a secure timestamp unless Apple's TSA was used).</summary>
    public DateTimeOffset? SigningTime { get; init; }
    /// <summary>Entitlements embedded in the signature (XML form), rendered as text values.</summary>
    public IReadOnlyDictionary<string, string> Entitlements { get; init; } = new Dictionary<string, string>();
    /// <summary>The same entitlements as parsed property-list values (Dictionary/List/string/bool/long…).</summary>
    public IReadOnlyDictionary<string, object> RawEntitlements { get; init; } = new Dictionary<string, object>();
    /// <summary>Problem reading the signature (truncated, malformed), when any.</summary>
    public string? Problem { get; init; }

    public static readonly CodeSignatureInfo None = new();
}

/// <summary>The embedded.mobileprovision of one bundle, as read and verified (CMS + chain to Apple, when the system trusts it).</summary>
public sealed record EmbeddedProfileInfo(AppleProvisioningProfileInfo? Profile, string? Problem)
{
    /// <summary>The profile's entitlements as parsed property-list values (for signing: arrays and dictionaries kept).</summary>
    public IReadOnlyDictionary<string, object> RawEntitlements { get; init; } = new Dictionary<string, object>();
}

/// <summary>Result of checking a bundle's sealed resources (_CodeSignature/CodeResources) against the files in the IPA.</summary>
public sealed record SealedResourcesInfo(bool Present, int Sealed, int Verified, IReadOnlyList<string> Modified, IReadOnlyList<string> Missing, IReadOnlyList<string> Unsealed)
{
    public bool Intact => Present && Modified.Count == 0 && Missing.Count == 0;
    public static readonly SealedResourcesInfo Absent = new(false, 0, 0, [], [], []);
}

/// <summary>One code bundle inside the IPA: the app itself, an extension, a framework, a dylib …</summary>
public sealed record BundleInfo
{
    /// <summary>Path relative to the .app (empty for the app itself), e.g. "PlugIns/Share.appex", "Frameworks/Lib.framework".</summary>
    public required string RelativePath { get; init; }
    public required BundleKind Kind { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? Name { get; init; }
    public string? ShortVersion { get; init; }
    public string? BuildNumber { get; init; }
    public string? ExecutableName { get; init; }
    /// <summary>MinimumOSVersion from Info.plist (falls back to the binary's LC_BUILD_VERSION).</summary>
    public string? MinimumOSVersion { get; init; }
    public IReadOnlyList<BinarySlice> Slices { get; init; } = [];
    public CodeSignatureInfo Signature { get; init; } = CodeSignatureInfo.None;
    public EmbeddedProfileInfo? EmbeddedProfile { get; init; }
    public SealedResourcesInfo Resources { get; init; } = SealedResourcesInfo.Absent;
    /// <summary>NSExtensionPointIdentifier for extensions.</summary>
    public string? ExtensionPoint { get; init; }

    public IEnumerable<string> Architectures => Slices.Select(s => s.Architecture).Distinct();
    public bool IsEncrypted => Slices.Any(s => s.Encrypted);
    /// <summary>Bundles that carry their own Info.plist identity and entitlements (app, extensions, watch apps, app clips).</summary>
    public bool TakesEntitlements => Kind is BundleKind.App or BundleKind.AppExtension or BundleKind.WatchApp or BundleKind.AppClip;
    public int Depth => RelativePath.Length == 0 ? 0 : RelativePath.Count(c => c == '/') + 1;
}

/// <summary>Device families from UIDeviceFamily.</summary>
[Flags]
public enum DeviceFamilies
{
    None = 0,
    IPhone = 1,
    IPad = 2,
    AppleTv = 4,
    Watch = 8,
    Vision = 16,
}

/// <summary>Everything HCG Impactor read from an IPA. The original file is never modified.</summary>
public sealed record IpaReport
{
    public required string SourcePath { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public DateTimeOffset InspectedAt { get; init; }
    /// <summary>"Payload/X.app".</summary>
    public string? AppPath { get; init; }
    public BundleInfo? App { get; init; }
    /// <summary>Every bundle and loose dylib inside the app, innermost first is NOT implied; use <see cref="SigningOrder"/>.</summary>
    public IReadOnlyList<BundleInfo> Nested { get; init; } = [];
    public DeviceFamilies Families { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public int FileCount { get; init; }
    public long UncompressedBytes { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IEnumerable<BundleInfo> AllBundles => App is null ? Nested : Nested.Prepend(App);
    public IEnumerable<BundleInfo> Extensions => Nested.Where(b => b.Kind is BundleKind.AppExtension or BundleKind.WatchApp or BundleKind.AppClip);
    public IEnumerable<BundleInfo> Frameworks => Nested.Where(b => b.Kind is BundleKind.Framework or BundleKind.Dylib);
    public bool CanProceed => App is not null && !Findings.HasBlockers();

    /// <summary>Signing order: deepest first, so every nested signature exists before its container is sealed.</summary>
    public IReadOnlyList<BundleInfo> SigningOrder =>
        Nested.OrderByDescending(b => b.Depth).ThenBy(b => b.Kind == BundleKind.Dylib ? 0 : 1).ThenBy(b => b.RelativePath, StringComparer.Ordinal)
            .Concat(App is null ? [] : [App]).ToList();
}
