using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HCG.ExecutableMaker.Packaging.Packagers;

public static partial class InfoPlist
{
    public static string Create(IReadOnlyList<(string Key, object Value)> entries)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        foreach (var (key, value) in entries)
        {
            sb.Append("    <key>").Append(SecurityElement.Escape(key)).Append("</key>\n");
            sb.Append(value switch
            {
                bool b => b ? "    <true/>\n" : "    <false/>\n",
                _ => $"    <string>{SecurityElement.Escape(value.ToString())}</string>\n",
            });
        }
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>Reads the top-level string/bool values of an XML plist (DTD processing disabled).</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
        using var reader = System.Xml.XmlReader.Create(path, settings);
        var doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict") ?? throw new InvalidDataException("Info.plist has no top-level dict.");
        var result = new Dictionary<string, string>();
        var children = dict.Elements().ToList();
        for (var i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Name != "key") continue;
            var v = children[i + 1];
            result[children[i].Value] = v.Name.LocalName switch { "true" => "true", "false" => "false", _ => v.Value };
            i++;
        }
        return result;
    }

    /// <summary>CFBundleShortVersionString must be up to three non-negative integers.</summary>
    public static string ToBundleVersion(string version)
    {
        var m = NumericVersionRegex().Match(version);
        if (!m.Success) return "1.0.0";
        var parts = m.Value.Split('.').Take(3).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts);
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,3}")]
    private static partial Regex NumericVersionRegex();
}
