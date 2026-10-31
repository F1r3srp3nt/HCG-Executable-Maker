using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine;

/// <summary>Extracts warnings and errors from MSBuild/dotnet console output, de-duplicating the end-of-build summary.</summary>
public sealed partial class MsBuildOutputParser
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public BuildMessage? Parse(string line)
    {
        var m = CanonicalRegex().Match(line);
        if (!m.Success) return null;
        var severity = m.Groups["sev"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        var file = m.Groups["file"].Value.Trim();
        int? lineNo = int.TryParse(m.Groups["line"].Value, out var l) ? l : null;
        var code = m.Groups["code"].Success && m.Groups["code"].Value.Length > 0 ? m.Groups["code"].Value : null;
        var message = m.Groups["msg"].Value.Trim();
        var key = $"{severity}|{code}|{file}|{lineNo}|{message}";
        if (!_seen.Add(key)) return null;
        return new BuildMessage(severity, message, code, string.IsNullOrEmpty(file) ? null : file, lineNo);
    }

    // e.g. "/src/App.cs(12,5): warning CS0168: The variable 'x' is declared but never used [/src/App.csproj]"
    //      "MSBUILD : error MSB1009: Project file does not exist."
    //      "/usr/share/dotnet/sdk/...targets(1,1): error NETSDK1045: The current .NET SDK does not support..."
    [GeneratedRegex(@"^\s*(?<file>[^:\r\n]*?(?:[A-Za-z]:[^:\r\n]*?)?)?(?:\((?<line>\d+)(?:,\d+)*\))?\s*:\s*(?<sev>warning|error)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalRegex();
}
