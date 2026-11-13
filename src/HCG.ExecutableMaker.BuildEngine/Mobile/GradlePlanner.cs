using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// Plans native Android builds through the project's own Gradle wrapper. The wrapper is started as
/// <c>java -classpath gradle/wrapper/gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain</c> — exactly what
/// <c>gradlew</c> does, without a shell. Gradle and Android user homes are HCG-managed, so personal
/// <c>~/.gradle/gradle.properties</c> credentials and keystores are never read.
/// </summary>
public static class GradlePlanner
{
    public static readonly string WrapperJar = Path.Combine("gradle", "wrapper", "gradle-wrapper.jar");
    public static readonly string WrapperProperties = Path.Combine("gradle", "wrapper", "gradle-wrapper.properties");

    public sealed record WrapperInfo(string JarPath, string? DistributionUrl, string? DistributionSha256, string JarSha256);

    public static MobilePlan Plan(MobilePlanInput input)
    {
        var o = input.Options;
        var android = input.Analysis.Android ?? throw new BuildValidationException("This project has no Android Gradle configuration.");
        var root = MobileArgs.Inside(input.SourceDirectory, android.Root);
        if (!android.HasWrapper || !File.Exists(Path.Combine(root, WrapperJar)))
            throw new BuildValidationException("The project has no Gradle wrapper (gradle/wrapper/gradle-wrapper.jar). HCG builds Android projects only with the project's own wrapper.");
        var module = o.Module is null ? android.AppModules.FirstOrDefault() : android.AppModules.FirstOrDefault(m => m.GradlePath == o.Module);
        if (module is null) throw new BuildValidationException(o.Module is null ? "No Android application module was found." : $"Module '{o.Module}' is not an Android application module of this project.");

        // The JDK and SDK that passed inspection (project readiness has already checked Gradle/AGP compatibility).
        var jdk = MobileArgs.InspectedJdk(input.Toolchains)
                  ?? throw new BuildValidationException("No working JDK was found. Choose one in Toolchains › Java (Android Studio's bundled JDK works).");
        var javaPath = jdk.JavaExecutable;
        var sdk = input.Toolchains.Find(ToolchainIds.AndroidSdk);
        if (sdk is not { IsAvailable: true, ExecutablePath: { } sdkPath }) throw new BuildValidationException("The Android SDK was not found. Choose it in Toolchains › Android SDK.");

        var (task, kind) = o.Output switch
        {
            MobileOutput.AndroidDebugApk => ("assemble", "debug"),
            MobileOutput.AndroidReleaseApk => ("assemble", "release"),
            MobileOutput.AndroidAppBundle or MobileOutput.AndroidAabToApk => ("bundle", "release"),
            _ => throw new BuildValidationException($"{o.Output.DisplayName()} is not an Android Gradle output."),
        };
        var variant = ResolveVariant(module, o.Variant, kind);
        var debugVariant = variant.EndsWith("Debug", StringComparison.Ordinal) || variant == "debug";

        var args = new List<string>
        {
            "-Xmx64m", "-Xms64m", "-Dorg.gradle.appname=gradlew",
            "-classpath", Path.Combine(root, WrapperJar), "org.gradle.wrapper.GradleWrapperMain",
            "--no-daemon", "--console=plain", "--no-configuration-cache",
        };
        var notes = new List<string>();
        if (!debugVariant)
        {
            switch (o.Signing)
            {
                case MobileSigningMode.DebugKeystore:
                    var ks = input.Keystore ?? throw new BuildValidationException("The HCG debug keystore was not prepared.");
                    // Android Studio's "injected signing" properties: sign this build with HCG's local debug key.
                    args.Add($"-Pandroid.injected.signing.store.file={ks.Path}");
                    args.Add($"-Pandroid.injected.signing.store.password={DebugKeystore.Password}");
                    args.Add($"-Pandroid.injected.signing.key.alias={DebugKeystore.Alias}");
                    args.Add($"-Pandroid.injected.signing.key.password={DebugKeystore.Password}");
                    notes.Add("Signed with HCG's local Android debug key (installable for testing; Google Play rejects debug-signed uploads).");
                    break;
                case MobileSigningMode.Unsigned when module.HasSigningConfig:
                    throw new BuildValidationException(
                        "This module defines its own release signing configuration, so Gradle will sign the release. HCG does not edit project files to remove it. " +
                        "Choose 'Project signing configuration' (credentials must come from the project itself, never from HCG), or 'HCG debug key' for a test build.");
                case MobileSigningMode.Unsigned:
                case MobileSigningMode.ProjectDefault when !module.HasSigningConfig:
                    notes.Add("No release signing is configured: Gradle produces an UNSIGNED release artifact, which cannot be installed until it is signed.");
                    break;
                case MobileSigningMode.ProjectDefault:
                    notes.Add("The project's own release signingConfig is used. HCG does not supply credentials: secret-like environment variables are removed and " +
                              "~/.gradle/gradle.properties is not read (HCG uses its own Gradle home). If the project expects credentials from there, signing fails.");
                    break;
                default:
                    throw new BuildValidationException($"Signing mode {o.Signing} does not apply to Android builds.");
            }
        }

        var taskPath = (module.GradlePath == ":" ? ":" : module.GradlePath + ":") + task + MobileArgs.Capitalize(variant);
        args.Add(taskPath);

        var gradleHome = Path.Combine(input.ToolHomeDirectory, "gradle");
        var androidHome = Path.Combine(input.ToolHomeDirectory, "android");
        Directory.CreateDirectory(gradleHome);
        Directory.CreateDirectory(androidHome);
        WriteProxyProperties(gradleHome, input.Environment);
        var env = MobileArgs.ToolchainEnvironment(input.Environment, input.Toolchains,
            ("GRADLE_USER_HOME", gradleHome), ("ANDROID_USER_HOME", androidHome));

        var moduleDir = module.Directory.Length == 0 ? root : MobileArgs.Inside(root, module.Directory);
        var outputs = task == "assemble"
            ? new OutputRule(PackagingFormat.AndroidApk, Path.Combine(moduleDir, "build", "outputs", "apk"), "*.apk", false)
            : new OutputRule(PackagingFormat.AndroidAab, Path.Combine(moduleDir, "build", "outputs", "bundle", variant), "*.aab", false);
        var step = new MobileStep(BuildStage.Compiling, $"Gradle {taskPath}", new ProcessSpec
        {
            Executable = javaPath, Arguments = args, WorkingDirectory = root, Environment = env,
        });
        return new MobilePlan([step], [outputs]) { Notes = notes };
    }

    /// <summary>Picks the requested variant, or the single variant of the requested build type.</summary>
    internal static string ResolveVariant(AndroidModuleInfo module, string? requested, string buildType)
    {
        if (requested is not null)
        {
            MobileArgs.RequireToken(requested, "variant", out var v, @"^[A-Za-z][A-Za-z0-9]{0,63}$");
            var matchesType = v.Equals(buildType, StringComparison.Ordinal) || v.EndsWith(MobileArgs.Capitalize(buildType), StringComparison.Ordinal);
            if (!matchesType && !(buildType == "release" && module.BuildTypes.Contains(v) && v != "debug"))
                throw new BuildValidationException($"Variant '{v}' is not a {buildType} variant.");
            return v;
        }
        var candidates = module.Variants.Where(x => x == buildType || x.EndsWith(MobileArgs.Capitalize(buildType), StringComparison.Ordinal)).ToList();
        return candidates.Count switch
        {
            0 => buildType,
            1 => candidates[0],
            _ => throw new BuildValidationException($"The module has several {buildType} variants ({string.Join(", ", candidates)}); choose one."),
        };
    }

    /// <summary>Reads the wrapper configuration shown in the trust prompt (where Gradle will be downloaded from).</summary>
    public static WrapperInfo? ReadWrapper(string gradleRoot)
    {
        var jar = Path.Combine(gradleRoot, WrapperJar);
        if (!File.Exists(jar)) return null;
        string? url = null, sha = null;
        var props = Path.Combine(gradleRoot, WrapperProperties);
        if (File.Exists(props))
        {
            foreach (var line in File.ReadLines(props))
            {
                var t = line.Trim();
                if (t.StartsWith("distributionUrl=", StringComparison.Ordinal)) url = t["distributionUrl=".Length..].Replace("\\:", ":");
                else if (t.StartsWith("distributionSha256Sum=", StringComparison.Ordinal)) sha = t["distributionSha256Sum=".Length..];
            }
        }
        return new WrapperInfo(jar, url, sha, Checksums.Sha256File(jar));
    }

    /// <summary>Warnings about the wrapper's distribution source. Non-HTTPS or unofficial hosts are flagged, not hidden.</summary>
    public static IReadOnlyList<string> AssessWrapper(WrapperInfo w)
    {
        var list = new List<string>();
        if (w.DistributionUrl is null) list.Add("gradle-wrapper.properties has no distributionUrl.");
        else if (!Uri.TryCreate(w.DistributionUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            list.Add($"The Gradle distribution is downloaded over an insecure or invalid URL: {w.DistributionUrl}");
        else if (uri.Host != "services.gradle.org")
            list.Add($"The Gradle distribution comes from {uri.Host}, not the official services.gradle.org.");
        if (w.DistributionSha256 is null) list.Add("No distributionSha256Sum is pinned, so the downloaded Gradle distribution is not checksum-verified by the wrapper.");
        return list;
    }

    /// <summary>Java ignores HTTPS_PROXY; translate host/port (never credentials) into HCG's Gradle home.</summary>
    private static void WriteProxyProperties(string gradleHome, IReadOnlyDictionary<string, string> env)
    {
        var file = Path.Combine(gradleHome, "gradle.properties");
        var lines = new List<string> { "# Written by HCG Executable Maker. Proxy host/port only; no credentials." };
        foreach (var (name, scheme) in new[] { ("HTTPS_PROXY", "https"), ("HTTP_PROXY", "http") })
        {
            var value = env.GetValueOrDefault(name) ?? env.GetValueOrDefault(name.ToLowerInvariant());
            if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var proxy) || proxy.Host.Length == 0) continue;
            lines.Add($"systemProp.{scheme}.proxyHost={proxy.Host}");
            lines.Add($"systemProp.{scheme}.proxyPort={(proxy.IsDefaultPort ? 80 : proxy.Port)}");
        }
        var noProxy = env.GetValueOrDefault("NO_PROXY") ?? env.GetValueOrDefault("no_proxy");
        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            var hosts = string.Join('|', noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => h.StartsWith('.') ? "*" + h : h));
            lines.Add($"systemProp.http.nonProxyHosts={hosts}");
            lines.Add($"systemProp.https.nonProxyHosts={hosts}");
        }
        File.WriteAllLines(file, lines);
    }
}
