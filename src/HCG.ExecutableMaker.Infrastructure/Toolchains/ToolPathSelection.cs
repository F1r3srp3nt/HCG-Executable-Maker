using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// HCG's own JDK and Android SDK choices (Toolchains › Java / Android SDK). A chosen path is normalized (a .jdk
/// bundle, an Android Studio app, a bin folder or the java executable all lead to the JDK home), verified by actually
/// running/inspecting it, and saved only when it works. Nothing machine-wide (JAVA_HOME, shell profiles) is changed.
/// </summary>
public sealed class ToolPathSelection(ISettingsStore settings, IProcessRunner runner)
{
    public sealed record JavaCheck(JdkCandidate Candidate, bool Saved);
    public sealed record SdkCheck(AndroidSdkDiscoveryResult Sdk, bool Saved);

    /// <summary>Turns what the user picked into a JAVA_HOME candidate.</summary>
    public static string NormalizeJavaHome(string path)
    {
        var p = path.Trim().TrimEnd('/', '\\');
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (File.Exists(p) && Path.GetFileName(p) is "java" or "java.exe") return JavaDiscovery.HomeOfExecutable(p);
        if (Path.GetFileName(p) == "bin" && File.Exists(Path.Combine(p, exe))) return Path.GetDirectoryName(p)!;
        if (p.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            foreach (var rel in new[] { "Contents/jbr/Contents/Home", "Contents/jre/Contents/Home", "Contents/jre/jdk/Contents/Home" })
                if (Directory.Exists(Path.Combine(p, rel))) return Path.Combine(p, rel);
        if (Directory.Exists(Path.Combine(p, "Contents", "Home"))) return Path.Combine(p, "Contents", "Home");
        return p;
    }

    /// <summary>Turns what the user picked into an Android SDK root (also accepts a component folder inside it).</summary>
    public static string NormalizeAndroidSdk(string path)
    {
        var p = path.Trim().TrimEnd('/', '\\');
        if (!AndroidSdkDiscovery.LooksLikeSdk(p) && Path.GetDirectoryName(p) is { } parent && AndroidSdkDiscovery.LooksLikeSdk(parent)
            && Path.GetFileName(p) is "platforms" or "build-tools" or "platform-tools" or "cmdline-tools" or "licenses")
            return parent;
        return p;
    }

    /// <summary>Verifies a JDK; saves it only when it is a working JDK (with javac).</summary>
    public async Task<JavaCheck> VerifyAndSaveJavaAsync(string picked, bool save, CancellationToken ct = default)
    {
        var home = NormalizeJavaHome(picked);
        var c = await new JavaDiscovery(runner).ProbeAsync(home, "Saved in HCG", HostInfo.CurrentPlatform, ct).ConfigureAwait(false);
        if (save && c.IsUsable)
        {
            var s = await settings.LoadAsync(ct).ConfigureAwait(false);
            await settings.SaveAsync(s with { JavaHome = c.Home }, ct).ConfigureAwait(false);
            return new JavaCheck(c, true);
        }
        return new JavaCheck(c, false);
    }

    /// <summary>Verifies an Android SDK folder; saves it only when it is an SDK.</summary>
    public async Task<SdkCheck> VerifyAndSaveAndroidSdkAsync(string picked, bool save, CancellationToken ct = default)
    {
        var root = NormalizeAndroidSdk(picked);
        var result = AndroidSdkDiscovery.LooksLikeSdk(root)
            ? AndroidSdkDiscovery.Inspect(root, "Saved in HCG")
            : new AndroidSdkDiscoveryResult
            {
                Root = root, Source = "Saved in HCG",
                Problem = Directory.Exists(root)
                    ? $"{root} is not an Android SDK (no platforms, build-tools, platform-tools or cmdline-tools folder). The SDK location is shown in Android Studio › Settings › Languages & Frameworks › Android SDK."
                    : $"{root} does not exist.",
            };
        if (save && result.Problem is null)
        {
            var s = await settings.LoadAsync(ct).ConfigureAwait(false);
            await settings.SaveAsync(s with { AndroidSdkPath = root }, ct).ConfigureAwait(false);
            return new SdkCheck(result, true);
        }
        return new SdkCheck(result, false);
    }

    public async Task ClearJavaAsync(CancellationToken ct = default)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        await settings.SaveAsync(s with { JavaHome = null }, ct).ConfigureAwait(false);
    }

    public async Task ClearAndroidSdkAsync(CancellationToken ct = default)
    {
        var s = await settings.LoadAsync(ct).ConfigureAwait(false);
        await settings.SaveAsync(s with { AndroidSdkPath = null }, ct).ConfigureAwait(false);
    }
}
