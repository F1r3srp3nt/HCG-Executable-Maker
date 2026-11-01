using System.Buffers.Binary;
using System.Text;

namespace HCG.ExecutableMaker.Packaging.Mobile;

/// <summary>A decoded manifest element: name and attributes (attribute names without namespace prefix).</summary>
public sealed record ManifestElement(string Name, IReadOnlyDictionary<string, string> Attributes, int Depth);

/// <summary>
/// Decoder for Android binary XML (AXML), the format of AndroidManifest.xml inside an APK. Bounds-checked throughout.
/// </summary>
public static class AndroidBinaryXml
{
    private const ushort XmlType = 0x0003, StringPoolType = 0x0001, ResourceMapType = 0x0180, StartElementType = 0x0102, EndElementType = 0x0103;
    private const uint NoIndex = 0xFFFFFFFF;

    // Framework attribute resource ids, used when attribute name strings are stripped.
    private static readonly Dictionary<uint, string> KnownAttributes = new()
    {
        [0x01010003] = "name", [0x01010001] = "label", [0x0101021b] = "versionCode", [0x0101021c] = "versionName",
        [0x0101020c] = "minSdkVersion", [0x01010270] = "targetSdkVersion", [0x0101000f] = "debuggable",
        [0x01010572] = "compileSdkVersion", [0x01010573] = "compileSdkVersionCodename",
    };

    public static IReadOnlyList<ManifestElement> Parse(byte[] data)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(data) != XmlType)
            throw new InvalidDataException("Not an Android binary XML document.");
        var strings = Array.Empty<string>();
        var resourceIds = Array.Empty<uint>();
        var elements = new List<ManifestElement>();
        var depth = 0;
        var pos = (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var end = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)), (uint)data.Length);
        while (pos + 8 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (size < 8 || pos + size > end || headerSize > size) throw new InvalidDataException("Corrupt chunk in binary XML.");
            var chunk = data.AsSpan(pos, size);
            switch (type)
            {
                case StringPoolType: strings = ReadStringPool(chunk); break;
                case ResourceMapType:
                    resourceIds = new uint[(size - headerSize) / 4];
                    for (var i = 0; i < resourceIds.Length; i++) resourceIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + i * 4)..]);
                    break;
                case StartElementType:
                    if (size < 36) throw new InvalidDataException("Truncated start element.");
                    var ext = chunk[16..];
                    var name = Str(strings, BinaryPrimitives.ReadUInt32LittleEndian(ext[4..]));
                    var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(ext[8..]);
                    var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(ext[10..]);
                    var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(ext[12..]);
                    var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (var i = 0; i < attrCount; i++)
                    {
                        var off = 16 + attrStart + i * attrSize;
                        if (attrSize < 20 || off + 20 > size) throw new InvalidDataException("Attribute outside of element chunk.");
                        var a = chunk[off..];
                        var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(a[4..]);
                        var attrName = Str(strings, nameIndex);
                        if ((attrName.Length == 0 || attrName.Contains(':')) && nameIndex < resourceIds.Length && KnownAttributes.TryGetValue(resourceIds[nameIndex], out var known))
                            attrName = known;
                        if (attrName.Length == 0) continue;
                        attrs[attrName] = Value(strings, BinaryPrimitives.ReadUInt32LittleEndian(a[8..]), a[15], BinaryPrimitives.ReadUInt32LittleEndian(a[16..]));
                    }
                    elements.Add(new ManifestElement(name, attrs, depth));
                    depth++;
                    break;
                case EndElementType: depth = Math.Max(0, depth - 1); break;
            }
            pos += size;
        }
        if (elements.Count == 0) throw new InvalidDataException("Binary XML contains no elements.");
        return elements;
    }

    private static string Value(string[] strings, uint raw, byte dataType, uint data) => dataType switch
    {
        0x03 => Str(strings, data),
        0x10 => ((int)data).ToString(System.Globalization.CultureInfo.InvariantCulture),
        0x11 => "0x" + data.ToString("x8"),
        0x12 => data != 0 ? "true" : "false",
        0x01 => raw != NoIndex ? Str(strings, raw) : "@0x" + data.ToString("x8"),
        _ => raw != NoIndex ? Str(strings, raw) : data.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Str(string[] strings, uint index) => index < strings.Length ? strings[index] : "";

    private static string[] ReadStringPool(ReadOnlySpan<byte> c)
    {
        if (c.Length < 28) throw new InvalidDataException("Truncated string pool.");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(c[16..]);
        var stringsStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[20..]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        var utf8 = (flags & (1 << 8)) != 0;
        if (count < 0 || count > 1_000_000 || headerSize + count * 4L > c.Length) throw new InvalidDataException("Invalid string pool size.");
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var off = stringsStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(c[(headerSize + i * 4)..]);
            if (off < 0 || off >= c.Length) throw new InvalidDataException("String pool offset out of range.");
            result[i] = utf8 ? Utf8At(c, off) : Utf16At(c, off);
        }
        return result;
    }

    private static string Utf16At(ReadOnlySpan<byte> c, int off)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(c[off..]);
        if ((len & 0x8000) != 0) { len = ((len & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(c[(off + 2)..]); off += 2; }
        off += 2;
        if (off + len * 2 > c.Length) throw new InvalidDataException("String exceeds pool.");
        return Encoding.Unicode.GetString(c.Slice(off, len * 2));
    }

    private static string Utf8At(ReadOnlySpan<byte> c, int off)
    {
        ReadUtf8Length(c, ref off); // UTF-16 length (unused)
        var len = ReadUtf8Length(c, ref off);
        if (off + len > c.Length) throw new InvalidDataException("String exceeds pool.");
        return Encoding.UTF8.GetString(c.Slice(off, len));
    }

    private static int ReadUtf8Length(ReadOnlySpan<byte> c, ref int o)
    {
        int l = c[o++];
        if ((l & 0x80) != 0) l = ((l & 0x7F) << 8) | c[o++];
        return l;
    }
}

/// <summary>Decoder for aapt2's protobuf XML (AndroidManifest.xml inside an App Bundle module). Minimal wire-format reader.</summary>
public static class ProtoXml
{
    public static IReadOnlyList<ManifestElement> Parse(byte[] data)
    {
        var list = new List<ManifestElement>();
        ParseNode(data, 0, list);
        if (list.Count == 0) throw new InvalidDataException("Protobuf manifest contains no elements.");
        return list;
    }

    private static void ParseNode(ReadOnlySpan<byte> node, int depth, List<ManifestElement> output)
    {
        if (depth > 64) throw new InvalidDataException("Manifest nesting is too deep.");
        foreach (var (field, _, value) in Fields(node))
            if (field == 1) ParseElement(value, depth, output);
    }

    private static void ParseElement(ReadOnlySpan<byte> element, int depth, List<ManifestElement> output)
    {
        var name = "";
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        var children = new List<byte[]>();
        foreach (var (field, _, value) in Fields(element))
        {
            switch (field)
            {
                case 3: name = Encoding.UTF8.GetString(value); break;
                case 4:
                    string attrName = "", attrValue = "";
                    foreach (var (f, wire, v) in Fields(value))
                    {
                        if (f == 2) attrName = Encoding.UTF8.GetString(v);
                        else if (f == 3) attrValue = Encoding.UTF8.GetString(v);
                        else if (f == 6 && attrValue.Length == 0) attrValue = CompiledItem(v) ?? "";
                    }
                    if (attrName.Length > 0) attrs[attrName] = attrValue;
                    break;
                case 5: children.Add(value.ToArray()); break;
            }
        }
        output.Add(new ManifestElement(name, attrs, depth));
        foreach (var child in children) ParseNode(child, depth + 1, output);
    }

    /// <summary>Item.prim (field 7): int_decimal (6), int_hex (7), boolean (8).</summary>
    private static string? CompiledItem(ReadOnlySpan<byte> item)
    {
        foreach (var (f, _, v) in Fields(item))
        {
            if (f != 7) continue;
            var r = 0;
            if (v.Length == 0) return null;
            var tag = (int)ReadVarint(v, ref r);
            var number = ReadVarint(v, ref r);
            return (tag >> 3) switch
            {
                6 => ((int)number).ToString(System.Globalization.CultureInfo.InvariantCulture),
                7 => "0x" + ((uint)number).ToString("x8"),
                8 => number != 0 ? "true" : "false",
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Iterates top-level fields; varint values are returned as their encoded bytes.</summary>
    public static List<(int Field, int Wire, byte[] Value)> Fields(ReadOnlySpan<byte> data)
    {
        var result = new List<(int, int, byte[])>();
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data, ref pos);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    var start = pos;
                    ReadVarint(data, ref pos);
                    result.Add((field, wire, data[start..pos].ToArray()));
                    break;
                case 1: Need(data, pos, 8); pos += 8; break;
                case 2:
                    var len = ReadVarint(data, ref pos);
                    if (len > (ulong)(data.Length - pos)) throw new InvalidDataException("Protobuf field exceeds message.");
                    result.Add((field, wire, data.Slice(pos, (int)len).ToArray()));
                    pos += (int)len;
                    break;
                case 5: Need(data, pos, 4); pos += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
        }
        return result;
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> d, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= d.Length) throw new InvalidDataException("Truncated protobuf varint.");
            var b = d[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Protobuf varint is too long.");
    }

    private static void Need(ReadOnlySpan<byte> d, int pos, int n)
    {
        if (pos + n > d.Length) throw new InvalidDataException("Truncated protobuf field.");
    }
}
