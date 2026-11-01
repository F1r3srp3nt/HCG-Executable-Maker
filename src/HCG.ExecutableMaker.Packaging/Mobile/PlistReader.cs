using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace HCG.ExecutableMaker.Packaging.Mobile;

/// <summary>
/// Reads Apple property lists in XML or binary (bplist00) form into Dictionary/List/string/long/bool/double/byte[]/DateTime.
/// Defensive: bounds-checked, depth-limited, no external entities.
/// </summary>
public static class PlistReader
{
    private const int MaxDepth = 64;

    public static object Parse(byte[] data)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00") return new Binary(data).Read();
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 16_000_000 };
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(data), settings);
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty property list.");
        var first = root.Elements().FirstOrDefault() ?? throw new InvalidDataException("Property list has no value.");
        return FromXml(first, 0);
    }

    public static Dictionary<string, object> ParseDictionary(byte[] data) =>
        Parse(data) as Dictionary<string, object> ?? throw new InvalidDataException("Property list root is not a dictionary.");

    private static object FromXml(XElement e, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Property list nesting is too deep.");
        switch (e.Name.LocalName)
        {
            case "dict":
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                var kids = e.Elements().ToList();
                for (var i = 0; i + 1 < kids.Count; i += 2)
                    if (kids[i].Name.LocalName == "key") d[kids[i].Value] = FromXml(kids[i + 1], depth + 1);
                return d;
            case "array": return e.Elements().Select(x => FromXml(x, depth + 1)).ToList();
            case "true": return true;
            case "false": return false;
            case "integer": return long.TryParse(e.Value.Trim(), out var l) ? l : 0L;
            case "real": return double.TryParse(e.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0d;
            case "date": return DateTime.TryParse(e.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTime.MinValue;
            case "data": try { return Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))); } catch (FormatException) { return Array.Empty<byte>(); }
            default: return e.Value;
        }
    }

    private sealed class Binary(byte[] d)
    {
        private int _offsetSize, _refSize;
        private long[] _offsets = [];

        public object Read()
        {
            if (d.Length < 40) throw new InvalidDataException("Binary plist is truncated.");
            var t = d.AsSpan(d.Length - 32);
            _offsetSize = t[6];
            _refSize = t[7];
            var count = BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            var top = BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            var tableOffset = BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            if (_offsetSize is < 1 or > 8 || _refSize is < 1 or > 8 || count == 0 || count > 1_000_000 || top >= count || tableOffset >= (ulong)d.Length)
                throw new InvalidDataException("Binary plist trailer is invalid.");
            _offsets = new long[count];
            for (ulong i = 0; i < count; i++)
            {
                var pos = (long)tableOffset + (long)i * _offsetSize;
                _offsets[i] = (long)ReadUInt(pos, _offsetSize);
                if (_offsets[i] >= d.Length) throw new InvalidDataException("Binary plist offset out of range.");
            }
            return Obj((long)top, 0);
        }

        private ulong ReadUInt(long pos, int size)
        {
            if (pos < 0 || pos + size > d.Length) throw new InvalidDataException("Binary plist read out of range.");
            ulong v = 0;
            for (var i = 0; i < size; i++) v = (v << 8) | d[pos + i];
            return v;
        }

        private object Obj(long index, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Binary plist nesting is too deep.");
            if (index < 0 || index >= _offsets.Length) throw new InvalidDataException("Binary plist reference out of range.");
            var pos = _offsets[index];
            var marker = d[pos];
            int type = marker >> 4, info = marker & 0xF;
            switch (type)
            {
                case 0x0: return info == 0x9 ? true : info == 0x8 ? false : (object)"";
                case 0x1: return (long)ReadUInt(pos + 1, 1 << info);
                case 0x2:
                    return info == 2 ? BitConverter.Int32BitsToSingle((int)ReadUInt(pos + 1, 4)) : BitConverter.Int64BitsToDouble((long)ReadUInt(pos + 1, 8));
                case 0x3: return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.Int64BitsToDouble((long)ReadUInt(pos + 1, 8)));
            }
            var (length, start) = Length(pos, info);
            switch (type)
            {
                case 0x4: Check(start, length); return d.AsSpan((int)start, (int)length).ToArray();
                case 0x5: Check(start, length); return Encoding.ASCII.GetString(d, (int)start, (int)length);
                case 0x6: Check(start, length * 2); return Encoding.BigEndianUnicode.GetString(d, (int)start, (int)length * 2);
                case 0x8: return (long)ReadUInt(pos + 1, info + 1);
                case 0xA:
                    Check(start, length * _refSize);
                    return Enumerable.Range(0, (int)length).Select(i => Obj((long)ReadUInt(start + i * _refSize, _refSize), depth + 1)).ToList();
                case 0xD:
                    Check(start, length * _refSize * 2);
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (var i = 0; i < length; i++)
                    {
                        var key = Obj((long)ReadUInt(start + i * _refSize, _refSize), depth + 1) as string ?? "";
                        dict[key] = Obj((long)ReadUInt(start + (length + i) * _refSize, _refSize), depth + 1);
                    }
                    return dict;
                default: throw new InvalidDataException($"Unsupported binary plist object type 0x{type:X}.");
            }
        }

        private (long Length, long Start) Length(long pos, int info)
        {
            if (info != 0xF) return (info, pos + 1);
            var intMarker = d[pos + 1];
            if (intMarker >> 4 != 0x1) throw new InvalidDataException("Binary plist length marker is invalid.");
            var size = 1 << (intMarker & 0xF);
            var len = (long)ReadUInt(pos + 2, size);
            if (len < 0 || len > d.Length) throw new InvalidDataException("Binary plist length is invalid.");
            return (len, pos + 2 + size);
        }

        private void Check(long start, long length)
        {
            if (start < 0 || length < 0 || start + length > d.Length) throw new InvalidDataException("Binary plist value out of range.");
        }
    }
}
