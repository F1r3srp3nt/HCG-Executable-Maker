using System.Globalization;
using System.Security;
using System.Text;

namespace HCG.Impactor.Signing;

/// <summary>Writes property-list values (as <c>PlistReader</c> returns them) as an XML plist, e.g. an entitlements file for codesign.</summary>
public static class PlistXmlWriter
{
    public static string Write(object root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        Value(sb, root, 0);
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    private static void Value(StringBuilder sb, object? v, int depth)
    {
        var pad = new string('\t', depth);
        switch (v)
        {
            case IReadOnlyDictionary<string, object> d: Dict(sb, d.OrderBy(kv => kv.Key, StringComparer.Ordinal), depth); break;
            case IDictionary<string, object> d: Dict(sb, d.OrderBy(kv => kv.Key, StringComparer.Ordinal), depth); break;
            case string s: sb.Append(pad).Append("<string>").Append(SecurityElement.Escape(s)).Append("</string>\n"); break;
            case bool b: sb.Append(pad).Append(b ? "<true/>" : "<false/>").Append('\n'); break;
            case long or int or short or byte: sb.Append(pad).Append("<integer>").Append(Convert.ToInt64(v, CultureInfo.InvariantCulture)).Append("</integer>\n"); break;
            case double or float: sb.Append(pad).Append("<real>").Append(Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n"); break;
            case DateTime dt: sb.Append(pad).Append("<date>").Append(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</date>\n"); break;
            case byte[] bytes: sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(bytes)).Append("</data>\n"); break;
            case System.Collections.IEnumerable list:
                sb.Append(pad).Append("<array>\n");
                foreach (var item in list) Value(sb, item, depth + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            default: sb.Append(pad).Append("<string>").Append(SecurityElement.Escape(v?.ToString() ?? "")).Append("</string>\n"); break;
        }
    }

    private static void Dict(StringBuilder sb, IEnumerable<KeyValuePair<string, object>> items, int depth)
    {
        var pad = new string('\t', depth);
        sb.Append(pad).Append("<dict>\n");
        foreach (var (k, val) in items)
        {
            sb.Append(pad).Append("\t<key>").Append(SecurityElement.Escape(k)).Append("</key>\n");
            Value(sb, val, depth + 1);
        }
        sb.Append(pad).Append("</dict>\n");
    }
}
