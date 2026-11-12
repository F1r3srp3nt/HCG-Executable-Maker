using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Theos (jailbreak packages for iOS): the Theos checkout, GNU make 4+, ldid, perl and an iOS-targeting clang
/// (Xcode's on a Mac, Theos' own toolchain on Linux). Detection only — HCG never installs Theos, SDKs or tools.
/// </summary>
public sealed partial class ToolchainDetector
{
    public const string TheosCategory = "Jailbreak packages (Theos)";
    public const string TheosSetupUrl = "https://theos.dev/docs/installation";

    private IEnumerable<Task<ToolchainInfo>> TheosTools(CancellationToken ct)
    {
        if (Host.Platform == TargetPlatform.Windows)
        {
            foreach (var (id, name) in new[] { (ToolchainIds.Theos, "Theos"), (ToolchainIds.TheosMake, "GNU make 4+"), (ToolchainIds.Ldid, "ldid"), (ToolchainIds.Perl, "perl"), (ToolchainIds.TheosIosToolchain, "iOS toolchain for Theos") })
                yield return Task.FromResult(Unsupported(id, name, TheosCategory, "Theos builds on macOS or Linux (on Windows: inside WSL, which HCG does not drive)."));
            yield break;
        }
        yield return Task.Run(DetectTheos, ct);
        yield return DetectGnuMake4Async(ct);
        yield return DetectLdidAsync(ct);
        yield return DetectSimpleTheosAsync(ToolchainIds.Perl, "perl", "perl", ["-v"], PerlVersionRegex(), "Theos' Logos preprocessor and packer are Perl scripts. macOS includes perl; on Linux: sudo apt install perl.", ct);
        yield return DetectTheosIosToolchainAsync(ct);
    }

    /// <summary>THEOS from the environment HCG was started with, else ~/theos or /opt/theos.</summary>
    public static string? FindTheos(string? environmentValue, string home)
    {
        foreach (var candidate in new[] { environmentValue, Path.Combine(home, "theos"), "/opt/theos" })
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, "makefiles", "common.mk"))) return Path.GetFullPath(candidate);
        return null;
    }

    /// <summary>iPhoneOS SDK versions in $THEOS/sdks (iPhoneOS16.5.sdk → "16.5"), newest first.</summary>
    public static IReadOnlyList<string> TheosSdks(string theos)
    {
        var dir = Path.Combine(theos, "sdks");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir, "iPhoneOS*.sdk").Select(d => Path.GetFileName(d)["iPhoneOS".Length..^".sdk".Length])
            .Where(v => Regex.IsMatch(v, @"^\d+(\.\d+)*$")).OrderByDescending(v => Version.TryParse(v.Contains('.') ? v : v + ".0", out var p) ? p : new Version(0, 0)).ToList();
    }

    private ToolchainInfo DetectTheos()
    {
        var hint = "Install Theos yourself (https://theos.dev/docs/installation) and set THEOS before starting HCG, or install it to ~/theos. " +
                   "Then place the iPhoneOS SDK your project's TARGET names in $THEOS/sdks. HCG never downloads Theos or SDKs.";
        var env = Environment.GetEnvironmentVariable("THEOS");
        var theos = FindTheos(env, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (theos is null)
            return Missing(ToolchainIds.Theos, "Theos", TheosCategory, hint) with
            {
                SetupUrl = TheosSetupUrl,
                Detail = env is { Length: > 0 } ? $"THEOS is set to {env}, but makefiles/common.mk is not there." : "Not found (THEOS is not set; ~/theos and /opt/theos do not exist).",
            };
        var sdks = TheosSdks(theos);
        var head = ReadGitHead(theos);
        var problems = new List<string>();
        if (theos.Contains(' ')) problems.Add("Its path contains a space, which Theos' makefiles do not support. Move Theos to a path without spaces.");
        if (!File.Exists(Path.Combine(theos, "bin", "dm.pl"))) problems.Add("bin/dm.pl (Theos' package builder) is missing: the checkout is incomplete (clone with --recursive).");
        if (!Directory.Exists(Path.Combine(theos, "vendor", "include")) && !Directory.Exists(Path.Combine(theos, "include")))
            problems.Add("vendor/include is missing: the checkout is incomplete (git submodule update --init --recursive).");
        return new ToolchainInfo
        {
            Id = ToolchainIds.Theos, DisplayName = "Theos", Category = TheosCategory,
            Status = problems.Count == 0 ? ToolchainStatus.Installed : ToolchainStatus.SetupRequired,
            ExecutablePath = theos, Version = head, Source = env is { Length: > 0 } ? "THEOS" : "Standard location",
            AdditionalVersions = sdks.Select(v => "iPhoneOS" + v + ".sdk").ToList(),
            Detail = problems.Count > 0 ? string.Join(" ", problems)
                : sdks.Count > 0 ? "SDKs in $THEOS/sdks: " + string.Join(", ", sdks.Select(v => "iPhoneOS " + v))
                : "No iPhoneOS SDKs in $THEOS/sdks yet.",
            InstallHint = hint, SetupUrl = TheosSetupUrl,
        };
    }

    private static string? ReadGitHead(string dir)
    {
        try
        {
            var head = Path.Combine(dir, ".git", "HEAD");
            if (!File.Exists(head)) return null;
            var text = File.ReadAllText(head).Trim();
            if (!text.StartsWith("ref: ", StringComparison.Ordinal)) return "commit " + text[..Math.Min(12, text.Length)];
            var refFile = Path.Combine(dir, ".git", text[5..]);
            return File.Exists(refFile) ? $"{text[5..].Replace("refs/heads/", "")} @ {File.ReadAllText(refFile).Trim()[..12]}" : text[5..].Replace("refs/heads/", "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
    }

    private async Task<ToolchainInfo> DetectGnuMake4Async(CancellationToken ct)
    {
        const string name = "GNU make 4+";
        var hint = Host.Platform == TargetPlatform.MacOS
            ? "macOS ships GNU make 3.81, which Theos does not support. Install GNU make 4 (brew install make provides gmake), then press Refresh."
            : "sudo apt install make (GNU make 4 or later).";
        foreach (var command in Host.Platform == TargetPlatform.MacOS ? new[] { "gmake", "make" } : ["make", "gmake"])
        {
            var exe = locator.Find(command, Host.Platform == TargetPlatform.MacOS ? [$"/opt/homebrew/bin/{command}", $"/usr/local/bin/{command}"] : []);
            if (exe is null) continue;
            if (Host.Platform == TargetPlatform.MacOS && exe.StartsWith("/usr/bin/", StringComparison.Ordinal) && !await DeveloperToolsPresentAsync(ct).ConfigureAwait(false)) continue;
            var info = await ProbeVersionAsync(ToolchainIds.TheosMake, name, TheosCategory, exe, ["--version"], MakeVersionRegex(), hint, ct).ConfigureAwait(false);
            if (!info.IsAvailable) continue;
            if (info.Version is { } v && int.TryParse(v.Split('.')[0], out var major) && major < 4)
            {
                if (command == "make") return info with { Status = ToolchainStatus.IncompatibleVersion, Detail = $"{exe} is GNU make {v}; Theos needs GNU make 4 or later.", InstallHint = hint };
                continue;
            }
            return info with { InstallHint = hint, Detail = $"Runs Theos' makefiles ({exe})." };
        }
        return Missing(ToolchainIds.TheosMake, name, TheosCategory, hint);
    }

    private async Task<ToolchainInfo> DetectLdidAsync(CancellationToken ct)
    {
        var hint = Host.Platform == TargetPlatform.MacOS
            ? "Install ldid (brew install ldid). Theos uses it to sign the binaries with their entitlements."
            : "Install ldid (ProcursusTeam/ldid releases, or your Theos installation's toolchain). Theos uses it to sign the binaries.";
        var theos = FindTheos(Environment.GetEnvironmentVariable("THEOS"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var exe = locator.Find("ldid", [.. (theos is null ? [] : new[] { Path.Combine(theos, "toolchain", "linux", "iphone", "bin", "ldid"), Path.Combine(theos, "bin", "ldid") }), "/opt/homebrew/bin/ldid", "/usr/local/bin/ldid"]);
        if (exe is null) return Missing(ToolchainIds.Ldid, "ldid", TheosCategory, hint);
        // ldid prints its version only in some builds; existence and a successful -h is enough to report it present.
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = ["-h"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var text = stdout + "\n" + stderr;
            return new ToolchainInfo
            {
                Id = ToolchainIds.Ldid, DisplayName = "ldid", Category = TheosCategory, Status = ToolchainStatus.Installed, ExecutablePath = exe,
                Version = Regex.Match(text, @"ldid[^\n]*?(\d+\.\d+(\.\d+)?)") is { Success: true } m ? m.Groups[1].Value : null,
                Detail = "Signs the built binaries (pseudo-signature with entitlements).", InstallHint = hint,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ToolchainIds.Ldid, "ldid", TheosCategory, exe, ex.Message, hint); }
    }

    private async Task<ToolchainInfo> DetectSimpleTheosAsync(string id, string name, string command, string[] args, Regex regex, string hint, CancellationToken ct)
    {
        var exe = locator.Find(command);
        if (exe is null) return Missing(id, name, TheosCategory, hint);
        return await ProbeVersionAsync(id, name, TheosCategory, exe, args, regex, hint, ct).ConfigureAwait(false) with { InstallHint = hint };
    }

    /// <summary>On a Mac Theos compiles with Xcode's clang (needed for the current arm64e ABI); on Linux with $THEOS/toolchain.</summary>
    private async Task<ToolchainInfo> DetectTheosIosToolchainAsync(CancellationToken ct)
    {
        const string name = "iOS toolchain for Theos";
        if (Host.Platform == TargetPlatform.MacOS)
        {
            var hint = "Install Xcode (App Store) and select it (Toolchains › Xcode). Theos compiles with Xcode's clang; it also produces the current arm64e ABI that iOS 14+ needs.";
            try
            {
                var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xcrun", Arguments = ["--sdk", "iphoneos", "--find", "clang"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
                if (!r.Succeeded) return Missing(ToolchainIds.TheosIosToolchain, name, TheosCategory, hint) with { Detail = "xcrun found no iOS clang: " + FirstLine(stderr + stdout) };
                return new ToolchainInfo
                {
                    Id = ToolchainIds.TheosIosToolchain, DisplayName = name, Category = TheosCategory, Status = ToolchainStatus.Installed, ExecutablePath = stdout.Trim(),
                    Detail = "Xcode's clang (Apple toolchain: current arm64e ABI).", Source = "Selected Xcode", InstallHint = hint,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ToolchainIds.TheosIosToolchain, name, TheosCategory, "/usr/bin/xcrun", ex.Message, hint); }
        }
        var linuxHint = "Install the iOS toolchain into $THEOS/toolchain (the Theos installer does this on Linux). Note: Linux toolchains emit the older arm64e ABI, which iOS 14+ arm64e processes reject; HCG's package check reports it.";
        var theos = FindTheos(Environment.GetEnvironmentVariable("THEOS"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var clang = theos is null ? null : Path.Combine(theos, "toolchain", "linux", "iphone", "bin", "clang");
        if (clang is null || !File.Exists(clang)) return Missing(ToolchainIds.TheosIosToolchain, name, TheosCategory, linuxHint) with { SetupUrl = TheosSetupUrl };
        return await ProbeVersionAsync(ToolchainIds.TheosIosToolchain, name, TheosCategory, clang, ["--version"], CompilerVersionRegex(), linuxHint, ct).ConfigureAwait(false) with
        {
            InstallHint = linuxHint, Detail = "Theos' Linux iOS toolchain. arm64e slices it produces use the pre-iOS-14 ABI.",
        };
    }

    [GeneratedRegex(@"perl 5[^\n]*?\(v(\d+\.\d+\.\d+)\)|This is perl (\d+)")] private static partial Regex PerlVersionRegex();
}
