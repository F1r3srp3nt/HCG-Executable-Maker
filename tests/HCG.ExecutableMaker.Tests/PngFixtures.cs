using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using SkiaSharp;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// PNG test images. <see cref="CgBI"/> writes Apple's optimized PNG variant the way Xcode's PNG compression does: a CgBI
/// chunk before IHDR, BGRA byte order with premultiplied alpha, raw deflate without a zlib header, and every PNG filter
/// type across the rows, so the converter's inflate, unfilter, channel swap and un-premultiply are all exercised.
/// </summary>
internal static class PngFixtures
{
    /// <summary>Straight-alpha RGBA pixels: gradients, a transparent corner and semi-transparent bands.</summary>
    public static byte[] Pixels(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
            px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
            px[i + 2] = (byte)((x + y) % 256);
            px[i + 3] = x < w / 4 && y < h / 4 ? (byte)0 : y % 3 == 0 ? (byte)128 : y % 3 == 1 ? (byte)37 : (byte)255;
        }
        return px;
    }

    public static byte[] Standard(int size, int? height = null)
    {
        var h = height ?? size;
        var px = Pixels(size, h);
        using var bmp = new SKBitmap(new SKImageInfo(size, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] CgBI(int w, int h)
    {
        var px = Pixels(w, h);
        var stride = w * 4;
        var stored = new byte[px.Length];
        for (var i = 0; i < px.Length; i += 4)
        {
            int r = px[i], g = px[i + 1], b = px[i + 2], a = px[i + 3];
            stored[i] = (byte)(b * a / 255); stored[i + 1] = (byte)(g * a / 255); stored[i + 2] = (byte)(r * a / 255); stored[i + 3] = (byte)a;   // BGRA, premultiplied
        }
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            var filter = (byte)(y % 5);
            raw.WriteByte(filter);
            for (var x = 0; x < stride; x++)
            {
                int cur = stored[y * stride + x], a = x >= 4 ? stored[y * stride + x - 4] : 0, b = y > 0 ? stored[(y - 1) * stride + x] : 0,
                    c = x >= 4 && y > 0 ? stored[(y - 1) * stride + x - 4] : 0;
                var pred = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                raw.WriteByte((byte)(cur - pred));
            }
        }
        using var z = new MemoryStream();
        using (var d = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(raw.ToArray());   // raw deflate: no zlib header
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "CgBI", [0x50, 0x00, 0x20, 0x02]);
        Chunk(png, "IHDR", ihdr);
        var idat = z.ToArray();
        Chunk(png, "IDAT", idat[..(idat.Length / 2)]);   // split over two IDAT chunks, as encoders do
        Chunk(png, "IDAT", idat[(idat.Length / 2)..]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        s.Write(buf);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(buf, PngIcon.Crc32(td));
        s.Write(buf);
    }
}
