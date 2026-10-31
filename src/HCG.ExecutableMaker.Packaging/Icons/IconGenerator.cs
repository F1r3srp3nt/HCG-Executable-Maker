using System.Buffers.Binary;
using SkiaSharp;

namespace HCG.ExecutableMaker.Packaging.Icons;

public sealed record GeneratedIcons(string SourceCopy, string? IcoPath, string? IcnsPath, IReadOnlyList<string> Warnings);

/// <summary>
/// Generates platform icon containers from a single source image. The source file is copied unmodified; generated
/// sizes are aspect-preserving (non-square art is centered on a transparent canvas) and never upscaled beyond the source.
/// </summary>
public static class IconGenerator
{
    public static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];

    // ICNS element types that carry PNG payloads.
    private static readonly (string Type, int Size)[] IcnsElements =
    [
        ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
        ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512),
    ];

    public static GeneratedIcons Generate(string sourcePath, string outputDirectory, string baseName, bool ico, bool icns)
    {
        Directory.CreateDirectory(outputDirectory);
        var warnings = new List<string>();
        var info = IconInspector.Inspect(sourcePath);
        if (!info.IsValid)
            throw new InvalidDataException("Icon is invalid: " + string.Join(" ", info.Errors));

        var sourceCopy = Path.Combine(outputDirectory, "source" + Path.GetExtension(sourcePath).ToLowerInvariant());
        File.Copy(sourcePath, sourceCopy, overwrite: true);

        using var bitmap = DecodeLargest(sourcePath, info) ?? throw new InvalidDataException("The icon image could not be decoded.");
        var sourceEdge = Math.Max(bitmap.Width, bitmap.Height);

        string? icoPath = null, icnsPath = null;
        if (ico)
        {
            if (info.Format == IconFormat.Ico)
            {
                icoPath = Path.Combine(outputDirectory, baseName + ".ico");
                File.Copy(sourcePath, icoPath, overwrite: true); // already the native format: use it verbatim
            }
            else
            {
                var sizes = IcoSizes.Where(s => s <= sourceEdge).ToList();
                if (sizes.Count == 0) sizes.Add(IcoSizes[0]);
                if (sizes.Max() < 256) warnings.Add($"ICO generated up to {sizes.Max()}px only (source is {sourceEdge}px).");
                icoPath = Path.Combine(outputDirectory, baseName + ".ico");
                File.WriteAllBytes(icoPath, BuildIco(sizes.Select(s => RenderPng(bitmap, s)).ToList(), sizes));
            }
        }
        if (icns)
        {
            if (info.Format == IconFormat.Icns)
            {
                icnsPath = Path.Combine(outputDirectory, baseName + ".icns");
                File.Copy(sourcePath, icnsPath, overwrite: true);
            }
            else
            {
                var elements = IcnsElements.Where(e => e.Size <= sourceEdge || e.Size == 16).ToList();
                if (sourceEdge < 1024) warnings.Add($"ICNS generated up to {elements.Max(e => e.Size)}px only (source is {sourceEdge}px).");
                icnsPath = Path.Combine(outputDirectory, baseName + ".icns");
                File.WriteAllBytes(icnsPath, BuildIcns(elements.Select(e => (e.Type, RenderPng(bitmap, e.Size))).ToList()));
            }
        }
        return new GeneratedIcons(sourceCopy, icoPath, icnsPath, warnings);
    }

    private static SKBitmap? DecodeLargest(string path, IconInfo info)
    {
        if (info.Format == IconFormat.Icns)
        {
            var largest = IconInspector.ReadIcnsElements(File.ReadAllBytes(path))
                .Where(e => IconInspector.IsPng(e.Payload))
                .Select(e => SKBitmap.Decode(e.Payload))
                .Where(b => b is not null)
                .OrderByDescending(b => b!.Width * b.Height)
                .ToList();
            foreach (var extra in largest.Skip(1)) extra!.Dispose();
            return largest.FirstOrDefault();
        }
        if (info.Format == IconFormat.Ico)
        {
            // Pick the largest ICO frame (PNG frames decode directly; Skia decodes BMP frames via the ICO codec).
            var data = File.ReadAllBytes(path);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            SKBitmap? best = null;
            for (var i = 0; i < count; i++)
            {
                var e = data.AsSpan(6 + i * 16, 16);
                var size = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
                var offset = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
                if (offset < 0 || size <= 0 || (long)offset + size > data.Length) continue;
                var payload = data.AsSpan(offset, size).ToArray();
                var bmp = IconInspector.IsPng(payload) ? SKBitmap.Decode(payload) : null;
                if (bmp is null) continue;
                if (best is null || bmp.Width * bmp.Height > best.Width * best.Height) { best?.Dispose(); best = bmp; }
                else bmp.Dispose();
            }
            return best ?? SKBitmap.Decode(path);
        }
        return SKBitmap.Decode(path);
    }

    /// <summary>Renders the source into a transparent square of the given size, preserving aspect ratio.</summary>
    internal static byte[] RenderPng(SKBitmap source, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        var dest = SKRect.Create((size - w) / 2f, (size - h) / 2f, w, h);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    internal static byte[] BuildIco(IReadOnlyList<byte[]> pngs, IReadOnlyList<int> sizes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        for (var i = 0; i < pngs.Count; i++)
        {
            var s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return ms.ToArray();
    }

    internal static byte[] BuildIcns(IReadOnlyList<(string Type, byte[] Png)> elements)
    {
        var total = 8 + elements.Sum(e => 8 + e.Png.Length);
        var buffer = new byte[total];
        "icns"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), (uint)total);
        var offset = 8;
        foreach (var (type, png) in elements)
        {
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(buffer, offset);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), (uint)(png.Length + 8));
            png.CopyTo(buffer, offset + 8);
            offset += 8 + png.Length;
        }
        return buffer;
    }
}
