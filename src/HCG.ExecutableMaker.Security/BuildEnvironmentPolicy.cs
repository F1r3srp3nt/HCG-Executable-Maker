using System.Collections;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.Security;

/// <summary>
/// Builds the environment for processes that execute untrusted project code (restore/build/publish).
/// Anything that looks like a credential, token, signing secret, or cloud/CI identity is removed, so that
/// HCG or founder secrets are never exposed to imported build scripts.
/// </summary>
public static partial class BuildEnvironmentPolicy
{
    /// <summary>Variable-name fragments that indicate secrets (case-insensitive substring match).</summary>
    public static readonly IReadOnlyList<string> SecretNameFragments =
    [
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PWD_", "APIKEY", "API_KEY", "PRIVATE", "CREDENTIAL",
        "CERT", "SIGNING", "NOTARY", "KEYCHAIN", "PAT", "SESSION", "COOKIE", "AUTH",
    ];

    /// <summary>Variable-name prefixes always removed.</summary>
    public static readonly IReadOnlyList<string> BlockedPrefixes =
    [
        "HCG_", "AWS_", "AZURE_", "ARM_", "GOOGLE_", "GCP_", "GH_", "GITHUB_", "GITLAB_", "NUGET_",
        "NPM_", "APPLE_", "ASC_", "APP_STORE_", "SSH_", "GPG_", "DOCKER_", "VAULT_", "OPENAI_", "ANTHROPIC_",
        // electron-builder code-signing certificates and their passwords (CSC_LINK, WIN_CSC_LINK, CSC_NAME, …).
        "CSC_", "WIN_CSC_",
    ];

    /// <summary>Exact names that are needed for tooling and are kept even if they match a pattern.</summary>
    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "HOME", "USERPROFILE", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "SystemRoot", "windir",
        "ComSpec", "PROGRAMFILES", "ProgramFiles(x86)", "ProgramData", "APPDATA", "LOCALAPPDATA", "USER", "USERNAME",
        "SHELL", "DOTNET_ROOT", "XDG_RUNTIME_DIR", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "SystemDrive", "HTTPS_PROXY", "HTTP_PROXY",
        "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME",
    };

    /// <summary>Variables set for every build to keep tooling deterministic and quiet.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["AVALONIA_TELEMETRY_OPTOUT"] = "1",
    };

    public sealed record ScrubResult(IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> RemovedNames);

    public static ScrubResult CreateUntrustedBuildEnvironment(IDictionary? source = null)
    {
        source ??= System.Environment.GetEnvironmentVariables();
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (IsSensitive(name)) { removed.Add(name); continue; }
            env[name] = value;
        }
        foreach (var (k, v) in Defaults) env[k] = v;
        removed.Sort(StringComparer.Ordinal);
        return new ScrubResult(env, removed);
    }

    public static bool IsSensitive(string name)
    {
        if (AlwaysKeep.Contains(name)) return false;
        var upper = name.ToUpperInvariant();
        if (BlockedPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal))) return true;
        if (KeyWordRegex().IsMatch(upper)) return true;
        foreach (var fragment in SecretNameFragments)
        {
            if (fragment == "PAT")
            {
                if (PatRegex().IsMatch(upper)) return true;
                continue;
            }
            if (upper.Contains(fragment, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // "KEY" as a standalone word (e.g. SIGNING_KEY, API_KEY, KEY_FILE) without matching e.g. "KEYBOARD".
    [GeneratedRegex(@"(^|_)KEYS?(_|$)")]
    private static partial Regex KeyWordRegex();

    [GeneratedRegex(@"(^|_)PAT(_|$)")]
    private static partial Regex PatRegex();
}
