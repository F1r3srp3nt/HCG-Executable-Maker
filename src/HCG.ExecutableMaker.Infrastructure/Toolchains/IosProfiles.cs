using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Reads iOS provisioning profiles (*.mobileprovision) without changing anything: name, team, App ID, expiry and
/// type (Development, Ad Hoc, App Store, Enterprise) from the plist embedded in the CMS envelope.
/// </summary>
public static class IosProfiles
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxFiles = 300;

    public static IEnumerable<string> Folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles");
        yield return Path.Combine(home, "Library", "MobileDevice", "Provisioning Profiles");
    }

    public static IReadOnlyList<IosProvisioningProfile> Read(IEnumerable<string> folders)
    {
        var list = new List<IosProvisioningProfile>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.mobileprovision").Take(MaxFiles).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length > MaxBytes) continue;
                    if (Parse(File.ReadAllBytes(f), f) is { } p && list.All(x => x.Uuid is null || x.Uuid != p.Uuid)) list.Add(p);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return list;
    }

    public static IosProvisioningProfile? Parse(byte[] data, string path)
    {
        var text = Encoding.Latin1.GetString(data);
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        var end = text.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start) return null;
        XElement dict;
        try
        {
            var xml = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text[start..(end + 8)]));
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
            dict = XDocument.Load(reader).Root?.Element("dict") ?? throw new System.Xml.XmlException("no dict");
        }
        catch (System.Xml.XmlException) { return null; }

        var top = Pairs(dict);
        var ent = top.GetValueOrDefault("Entitlements") is { Name.LocalName: "dict" } e ? Pairs(e) : [];
        string? Str(Dictionary<string, XElement> d, string k) => d.GetValueOrDefault(k) is { Name.LocalName: "string" } x ? x.Value : null;
        var team = top.GetValueOrDefault("TeamIdentifier")?.Elements("string").FirstOrDefault()?.Value;
        var hasDevices = top.ContainsKey("ProvisionedDevices");
        var allDevices = top.GetValueOrDefault("ProvisionsAllDevices")?.Name.LocalName == "true";
        var getTaskAllow = ent.GetValueOrDefault("get-task-allow")?.Name.LocalName == "true";
        var kind = allDevices ? IosProfileKind.Enterprise
            : hasDevices ? (getTaskAllow ? IosProfileKind.Development : IosProfileKind.AdHoc)
            : IosProfileKind.AppStore;
        DateTimeOffset? expires = DateTimeOffset.TryParse(top.GetValueOrDefault("ExpirationDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
        return new IosProvisioningProfile(path, Str(top, "Name"), Str(top, "UUID"), team, Str(ent, "application-identifier"), kind, expires);
    }

    private static Dictionary<string, XElement> Pairs(XElement dict)
    {
        var d = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var kids = dict.Elements().ToList();
        for (var i = 0; i + 1 < kids.Count; i += 2)
            if (kids[i].Name.LocalName == "key") d[kids[i].Value] = kids[i + 1];
        return d;
    }
}
