using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.Security;

/// <summary>
/// Removes credentials from text before it is logged, exported as diagnostics, attached to a support request or sent
/// to HCG AI. Pattern-based: it catches common token and key formats, and it never guarantees that no secret
/// remains. Callers show the redacted text to the user for review before anything leaves the machine.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "[REDACTED]";

    public sealed record Finding(string Kind, int Line);

    private static readonly (string Kind, Regex Pattern, int Group)[] Patterns =
    [
        ("Private key block", PrivateKeyRegex(), 0),
        ("Bearer token", BearerRegex(), 1),
        ("JSON Web Token", JwtRegex(), 0),
        ("AWS access key", AwsKeyRegex(), 0),
        ("GitHub token", GitHubTokenRegex(), 0),
        ("Slack token", SlackTokenRegex(), 0),
        ("Stripe key", StripeKeyRegex(), 0),
        ("Google API key", GoogleKeyRegex(), 0),
        ("OpenAI/Anthropic key", AiKeyRegex(), 0),
        ("npm token", NpmTokenRegex(), 0),
        ("URL credentials", UrlCredentialsRegex(), 1),
        ("Secret assignment", AssignmentRegex(), 2),
    ];

    /// <summary>Text with every recognised secret replaced by <see cref="Mask"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;
        foreach (var (_, pattern, group) in Patterns)
            result = pattern.Replace(result, m => group == 0 ? Mask : m.Value.Replace(m.Groups[group].Value, Mask));
        return result;
    }

    /// <summary>What kinds of secrets the text appears to contain, with 1-based line numbers (for explaining flags).</summary>
    public static IReadOnlyList<Finding> Scan(string? text)
    {
        var findings = new List<Finding>();
        if (string.IsNullOrEmpty(text)) return findings;
        foreach (var (kind, pattern, group) in Patterns)
            foreach (Match m in pattern.Matches(text))
            {
                if (group > 0 && m.Groups[group].Value.Length < 6) continue;
                var line = 1 + text.AsSpan(0, m.Index).Count('\n');
                findings.Add(new Finding(kind, line));
            }
        return findings.DistinctBy(f => (f.Kind, f.Line)).OrderBy(f => f.Line).ToList();
    }

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----|$)")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)\b(?:bearer|token)\s+([A-Za-z0-9._~+/=-]{16,})")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\b(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{16,}\b")]
    private static partial Regex StripeKeyRegex();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}\b")]
    private static partial Regex AiKeyRegex();

    [GeneratedRegex(@"\bnpm_[A-Za-z0-9]{36}\b")]
    private static partial Regex NpmTokenRegex();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s:/@]+:([^\s@/]{3,})@")]
    private static partial Regex UrlCredentialsRegex();

    // KEY = value / "key": "value" for names that look secret (password, secret, token, api_key, private_key, …).
    [GeneratedRegex(@"(?im)([""']?[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|private[_-]?key|access[_-]?key|client[_-]?secret|auth)[A-Za-z0-9_.-]*[""']?\s*[:=]\s*)[""']?([^\s""',;]{6,})")]
    private static partial Regex AssignmentRegex();
}
