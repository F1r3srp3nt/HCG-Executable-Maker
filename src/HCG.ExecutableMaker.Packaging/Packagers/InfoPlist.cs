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

    /// <summary>
    /// Starts from a template XML plist (e.g. a packaging contract's Info.plist) and sets <paramref name="entries"/> on
    /// it: those keys take HCG's values, every other template key (arrays and dictionaries included) is kept as written,
    /// and keys the template lacks are appended. Comments are dropped; DTD processing is disabled.
    /// </summary>
    public static string Merge(string templatePath, IReadOnlyList<(string Key, object Value)> entries)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        XDocument doc;
        using (var reader = System.Xml.XmlReader.Create(templatePath, settings)) doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict") ?? throw new InvalidDataException("The Info.plist template has no top-level dict.");
        var children = dict.Elements().ToList();
        var pairs = new List<(string Key, XElement Value)>();
        for (var i = 0; i + 1 < children.Count; i += 2)
        {
            if (children[i].Name != "key") throw new InvalidDataException($"The Info.plist template has a value without a key near <{children[i].Name}>.");
            pairs.Add((children[i].Value, children[i + 1]));
        }
        var set = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        static XElement Value(object v) => v switch
        {
            bool b => new XElement(b ? "true" : "false"),
            _ => new XElement("string", v.ToString()),
        };
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        void Write(string key, XElement value)
        {
            sb.Append("    <key>").Append(SecurityElement.Escape(key)).Append("</key>\n");
            foreach (var line in value.ToString(SaveOptions.None).Split('\n'))
                sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }
        foreach (var (key, value) in pairs)
            Write(key, set.TryGetValue(key, out var hcg) ? Value(hcg) : value);
        foreach (var (key, value) in entries)
            if (pairs.All(p => p.Key != key)) Write(key, Value(value));
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
