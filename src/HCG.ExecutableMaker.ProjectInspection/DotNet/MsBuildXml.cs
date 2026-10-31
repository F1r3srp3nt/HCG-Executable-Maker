using System.Xml;
using System.Xml.Linq;

namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

internal static class MsBuildXml
{
    /// <summary>Loads XML with DTDs and external resolution disabled (prevents XXE and entity expansion attacks).</summary>
    public static XDocument Load(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20_000_000,
            IgnoreComments = true,
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    public static IEnumerable<XElement> Elements(XContainer container, string localName) =>
        container.Descendants().Where(e => e.Name.LocalName == localName);
}
