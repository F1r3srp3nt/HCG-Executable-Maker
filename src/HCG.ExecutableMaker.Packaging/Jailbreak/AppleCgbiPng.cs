using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Apple's CgBI PNG variant, which Xcode writes for the loose app icons in a built .app (pngcrush -iphone): a <c>CgBI</c>
/// chunk before <c>IHDR</c>, IDAT holding raw deflate without the zlib header, and pixels in BGRA order with
/// premultiplied alpha. Only Apple's decoders read it; HCG Repo, Sileo, Zebra and browsers do not. This converts it to a
/// standard RGBA PNG.
/// </summary>
public static class AppleCgbiPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxSide = 4096;

    /// <summary>A PNG signature followed by a <c>CgBI</c> chunk (bytes 12–16).</summary>
    public static bool IsCgbi(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[..8].SequenceEqual(Signature) && data.Slice(12, 4).SequenceEqual("CgBI"u8);

    /// <summary>Width and height from a standard PNG's IHDR (signature, then IHDR at bytes 12–16), or null.</summary>
    public static (int Width, int Height)? StandardSize(ReadOnlySpan<byte> data) =>
        data.Length >= 24 && data[..8].SequenceEqual(Signature) && data.Slice(12, 4).SequenceEqual("IHDR"u8)
            ? ((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..]))
            : null;

    /// <summary>
    /// Converts a CgBI PNG to a standard 8-bit RGBA PNG (filter 0, zlib). Throws <see cref="InvalidDataException"/> with the
    /// reason when the file is not CgBI or uses a layout Xcode does not produce (interlaced, not 8-bit RGB/RGBA).
    /// </summary>
    public static byte[] ToStandardPng(ReadOnlySpan<byte> data)
    {
        if (!IsCgbi(data)) throw new InvalidDataException("not an Apple CgBI PNG");
        int width = 0, height = 0, colorType = -1;
        var idat = new MemoryStream();
        var pos = 8;
        var ended = false;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length) throw new InvalidDataException("truncated chunk");
            var type = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
            var body = data.Slice(pos + 8, (int)length);
            pos += 12 + (int)length;
            switch (type)
            {
                case "IHDR":
                    if (body.Length != 13) throw new InvalidDataException("bad IHDR");
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    var depth = body[8];
                    colorType = body[9];
                    if (depth != 8 || colorType is not (2 or 6)) throw new InvalidDataException($"unsupported CgBI layout (bit depth {depth}, color type {colorType}); expected 8-bit RGB or RGBA");
                    if (body[10] != 0 || body[11] != 0) throw new InvalidDataException("unsupported compression or filter method");
                    if (body[12] != 0) throw new InvalidDataException("interlaced CgBI PNGs are not supported");
                    if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide) throw new InvalidDataException($"unsupported size {width}x{height}");
                    break;
                case "IDAT": idat.Write(body); break;
                case "IEND": ended = true; break;
            }
            if (ended) break;
        }
        if (colorType < 0) throw new InvalidDataException("no IHDR");
        if (idat.Length == 0) throw new InvalidDataException("no IDAT");

        var channels = colorType == 6 ? 4 : 3;
        var stride = width * channels;
        var raw = Inflate(idat.ToArray(), (stride + 1) * height);
        var pixels = Unfilter(raw, width, height, channels);

        // BGR(A) premultiplied → RGBA straight.
        var rgba = new byte[width * height * 4];
        for (int i = 0, o = 0; i < pixels.Length; i += channels, o += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = channels == 4 ? pixels[i + 3] : 255;
            if (a is 0) { r = g = b = 0; }
            else if (a < 255)
            {
                r = Math.Min(255, (r * 255 + a / 2) / a);
                g = Math.Min(255, (g * 255 + a / 2) / a);
                b = Math.Min(255, (b * 255 + a / 2) / a);
            }
            rgba[o] = (byte)r; rgba[o + 1] = (byte)g; rgba[o + 2] = (byte)b; rgba[o + 3] = (byte)a;
        }
        return EncodeRgba(width, height, rgba);
    }

    /// <summary>A standard 8-bit RGBA, non-interlaced PNG: IHDR, one IDAT (filter 0 on every row, zlib), IEND.</summary>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("pixel buffer does not match the size", nameof(rgba));
        var stride = width * 4;
        var scanlines = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++) rgba.Slice(y * stride, stride).CopyTo(scanlines.AsSpan(y * (stride + 1) + 1));
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(scanlines);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA; compression, filter, interlace 0
        var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)body.Length);
        s.Write(u32);
        var typeAndBody = new byte[4 + body.Length];
        Encoding.ASCII.GetBytes(type, typeAndBody);
        body.CopyTo(typeAndBody.AsSpan(4));
        s.Write(typeAndBody);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc32.Compute(typeAndBody));
        s.Write(u32);
    }

    /// <summary>CgBI IDAT is raw deflate (wbits -15); a zlib stream is accepted too, as some tools leave the header in.</summary>
    private static byte[] Inflate(byte[] data, int expected)
    {
        foreach (var zlib in new[] { false, true })
        {
            try
            {
                using var input = new MemoryStream(data);
                using Stream inflater = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                var output = new byte[expected];
                var read = 0;
                while (read < expected)
                {
                    var n = inflater.Read(output, read, expected - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read == expected) return output;
            }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("the IDAT data does not inflate to the expected size");
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int bpp)
    {
        var stride = width * bpp;
        var output = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            var cur = output.AsSpan(y * stride, stride);
            var prev = y > 0 ? output.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0, b = y > 0 ? prev[x] : 0, c = x >= bpp && y > 0 ? prev[x - bpp] : 0;
                cur[x] = (byte)(line[x] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"unknown PNG filter {filter} on row {y}"),
                });
            }
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
