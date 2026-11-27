using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Icons for HCG Repo, Sileo, Zebra and browsers: standard PNG only. Xcode stores app icons as Apple's "CgBI" PNG variant
/// (a CgBI chunk before IHDR, raw deflate without a zlib header, BGRA byte order, premultiplied alpha). Only Apple's own
/// frameworks decode it; browsers and Sileo cannot, so such an icon is converted to a standard RGBA PNG and the result
/// is validated with an independent, standard decoder (Skia) before it is used.
/// </summary>
public static class PngIcon
{
    public enum Kind { StandardPng, AppleCgBI, NotPng, Damaged }

    public sealed record Inspection(Kind Kind, int Width, int Height, byte BitDepth, byte ColorType, byte Interlace, string Detail);

    /// <summary>A usable icon: standard PNG bytes, where they came from and what was done to them.</summary>
    public sealed record Prepared(byte[] Png, int Width, int Height, string Source, string Detail);

    public sealed record Choice(Prepared? Icon, IReadOnlyList<string> Notes);

    /// <summary>Icons smaller than this are refused (HCG Repo and package managers draw icons at up to 88 points).</summary>
    public const int MinimumSize = 60;
    public const int RecommendedSize = 120;
    /// <summary>Approved artwork larger than this is scaled down (square, high-quality filter); the original is unchanged.</summary>
    public const int MaximumSize = 1024;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private sealed record Chunk(string Type, byte[] Data, bool CrcValid);

    private static List<Chunk>? ReadChunks(byte[] png, out string? problem)
    {
        problem = null;
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) { problem = "not a PNG file (no PNG signature)"; return null; }
        var chunks = new List<Chunk>();
        var at = 8;
        while (at + 12 <= png.Length)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at, 4));
            if (len > int.MaxValue || at + 12 + (long)len > png.Length) { problem = "a chunk runs past the end of the file (truncated)"; return chunks; }
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, (int)len).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + (int)len, 4));
            chunks.Add(new Chunk(type, data, Crc32(png.AsSpan(at + 4, 4 + (int)len)) == crc));
            at += 12 + (int)len;
            if (type == "IEND") return chunks;
        }
        problem = "no IEND chunk (truncated)";
        return chunks;
    }

    public static Inspection Inspect(byte[] png)
    {
        var chunks = ReadChunks(png, out var problem);
        if (chunks is null) return new(Kind.NotPng, 0, 0, 0, 0, 0, problem!);
        var ihdr = chunks.FirstOrDefault(c => c.Type == "IHDR");
        if (ihdr is null || ihdr.Data.Length < 13) return new(Kind.Damaged, 0, 0, 0, 0, 0, "no IHDR chunk");
        var w = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(0, 4));
        var h = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(4, 4));
        var (depth, color, interlace) = (ihdr.Data[8], ihdr.Data[9], ihdr.Data[12]);
        if (chunks.Any(c => c.Type == "CgBI"))
            return new(Kind.AppleCgBI, w, h, depth, color, interlace, $"Apple CgBI PNG ({w}×{h}): Xcode's optimized format, which browsers, Sileo and Zebra cannot decode");
        if (problem is not null) return new(Kind.Damaged, w, h, depth, color, interlace, problem);
        if (chunks[0].Type != "IHDR") return new(Kind.Damaged, w, h, depth, color, interlace, $"the first chunk is {chunks[0].Type}, not IHDR");
        if (chunks.FirstOrDefault(c => !c.CrcValid) is { } bad) return new(Kind.Damaged, w, h, depth, color, interlace, $"chunk {bad.Type} has a wrong CRC");
        return new(Kind.StandardPng, w, h, depth, color, interlace, $"standard PNG ({w}×{h})");
    }

    /// <summary>
    /// Standard-PNG validation: structure, CRCs, zlib stream, then a full decode by Skia (a standard decoder, like a
    /// browser's) with matching dimensions; square; at least <see cref="MinimumSize"/> pixels. Null when valid.
    /// </summary>
    public static string? ValidateStandard(byte[] png)
    {
        var i = Inspect(png);
        if (i.Kind != Kind.StandardPng) return i.Detail;
        try
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap is null) return "a standard PNG decoder cannot decode it";
            if (bitmap.Width != i.Width || bitmap.Height != i.Height) return $"decoded as {bitmap.Width}×{bitmap.Height}, but IHDR says {i.Width}×{i.Height}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "a standard PNG decoder cannot decode it: " + ex.Message; }
        if (i.Width != i.Height) return $"not square ({i.Width}×{i.Height})";
        if (i.Width < MinimumSize) return $"too small ({i.Width}×{i.Height}; at least {MinimumSize}×{MinimumSize})";
        return null;
    }

    /// <summary>Converts an Apple CgBI PNG to a standard RGBA (or RGB) PNG: inflate, unfilter, BGRA→RGBA, un-premultiply, re-encode.</summary>
    public static byte[] ConvertCgBI(byte[] cgbi)
    {
        var chunks = ReadChunks(cgbi, out var problem) ?? throw new InvalidDataException(problem);
        if (problem is not null) throw new InvalidDataException(problem);
        if (!chunks.Any(c => c.Type == "CgBI")) throw new InvalidDataException("not a CgBI PNG");
        var ihdr = chunks.First(c => c.Type == "IHDR").Data;
        var w = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4));
        var h = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4));
        var (depth, color, interlace) = (ihdr[8], ihdr[9], ihdr[12]);
        if (depth != 8 || color is not (6 or 2)) throw new InvalidDataException($"unsupported CgBI layout (bit depth {depth}, colour type {color}); only 8-bit RGBA/RGB occurs in Xcode icons");
        if (interlace != 0) throw new InvalidDataException("interlaced CgBI PNG is not supported");
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) throw new InvalidDataException($"implausible size {w}×{h}");
        var bpp = color == 6 ? 4 : 3;
        var stride = w * bpp;

        using var idat = new MemoryStream();
        foreach (var c in chunks.Where(c => c.Type == "IDAT")) idat.Write(c.Data);
        var raw = Inflate(idat.ToArray(), (stride + 1) * h);
        if (raw.Length != (stride + 1) * h) throw new InvalidDataException($"image data is {raw.Length} bytes, expected {(stride + 1) * h}");

        var pixels = Unfilter(raw, h, stride, bpp);
        for (var p = 0; p < pixels.Length; p += bpp)
        {
            (pixels[p], pixels[p + 2]) = (pixels[p + 2], pixels[p]);  // BGR(A) → RGB(A)
            if (bpp == 4)
            {
                var a = pixels[p + 3];
                if (a == 0) { pixels[p] = pixels[p + 1] = pixels[p + 2] = 0; continue; }
                if (a == 255) continue;
                for (var k = 0; k < 3; k++) pixels[p + k] = (byte)Math.Min(255, (pixels[p + k] * 255 + a / 2) / a);   // un-premultiply
            }
        }
        return Encode(w, h, color, pixels, stride);
    }

    /// <summary>
    /// Chooses the icon for a handoff: the app's own icon (converted when it is CgBI), and, when that is missing or cannot
    /// be made valid, the artwork the owner approved (<paramref name="approvedArtwork"/>, typically the 1024-pixel image of
    /// the project's AppIcon set). Every candidate must pass <see cref="ValidateStandard"/>; nothing invalid is used.
    /// </summary>
    public static Choice Choose(string? appIcon, string? approvedArtwork)
    {
        var notes = new List<string>();
        if (appIcon is not null && File.Exists(appIcon))
        {
            var bytes = File.ReadAllBytes(appIcon);
            var name = Path.GetFileName(appIcon);
            var i = Inspect(bytes);
            try
            {
                if (i.Kind == Kind.AppleCgBI)
                {
                    var png = ConvertCgBI(bytes);
                    if (ValidateStandard(png) is { } bad) notes.Add($"the app's icon {name} is Apple CgBI; the converted PNG is not usable: {bad}");
                    else return new(new Prepared(png, i.Width, i.Height, $"app bundle {name}", $"converted from Apple CgBI to a standard RGBA PNG ({i.Width}×{i.Height}) and verified with a standard decoder"), notes);
                }
                else if (ValidateStandard(bytes) is { } bad) notes.Add($"the app's icon {name} is not usable: {bad}");
                else return new(new Prepared(bytes, i.Width, i.Height, $"app bundle {name}", $"standard PNG ({i.Width}×{i.Height}), used unchanged"), notes);
            }
            catch (InvalidDataException ex) { notes.Add($"the app's icon {name} could not be converted: {ex.Message}"); }
        }
        else notes.Add("the app bundle has no loose AppIcon*.png (its icons are only in Assets.car)");

        if (approvedArtwork is { Length: > 0 })
        {
            if (!File.Exists(approvedArtwork)) { notes.Add($"the approved artwork {approvedArtwork} does not exist"); return new(null, notes); }
            var bytes = File.ReadAllBytes(approvedArtwork);
            var i = Inspect(bytes);
            try
            {
                if (i.Kind == Kind.AppleCgBI) { bytes = ConvertCgBI(bytes); i = Inspect(bytes); }
                if (ValidateStandard(bytes) is { } bad) { notes.Add($"the approved artwork {Path.GetFileName(approvedArtwork)} is not usable: {bad}"); return new(null, notes); }
                var detail = $"approved source artwork, standard PNG ({i.Width}×{i.Height})";
                if (i.Width > MaximumSize) { bytes = Resize(bytes, MaximumSize); detail += $", scaled to {MaximumSize}×{MaximumSize}"; i = Inspect(bytes); }
                return new(new Prepared(bytes, i.Width, i.Height, $"approved artwork {Path.GetFileName(approvedArtwork)}", detail), notes);
            }
            catch (InvalidDataException ex) { notes.Add($"the approved artwork could not be read: {ex.Message}"); }
        }
        else notes.Add("no approved source artwork was given (Unleashify › Package › Icon artwork, or --icon-artwork)");
        return new(null, notes);
    }

    /// <summary>
    /// The largest image of the project's own AppIcon set (Assets.xcassets/AppIcon.appiconset), as a suggestion for the
    /// owner to approve. Returns null when the project has none. Never used without the owner's choice.
    /// </summary>
    public static string? SuggestArtwork(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) return null;
        try
        {
            return Directory.EnumerateDirectories(projectRoot, "*.appiconset", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 })
                .Where(d => !d.Contains("/build/", StringComparison.Ordinal) && !d.Contains("DerivedData", StringComparison.Ordinal))
                .OrderBy(d => Path.GetFileName(d).StartsWith("AppIcon", StringComparison.Ordinal) ? 0 : 1)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.png"))
                .Select(f => (f, i: Inspect(File.ReadAllBytes(f))))
                .Where(x => x.i.Kind == Kind.StandardPng && x.i.Width == x.i.Height && x.i.Width >= MinimumSize)
                .OrderByDescending(x => x.i.Width).Select(x => x.f).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static byte[] Resize(byte[] png, int size)
    {
        using var src = SKBitmap.Decode(png) ?? throw new InvalidDataException("cannot decode the artwork");
        using var dst = src.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                        ?? throw new InvalidDataException("cannot scale the artwork");
        using var image = SKImage.FromBitmap(dst);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Inflate(byte[] data, int expected)
    {
        // CgBI image data is raw deflate (no zlib header). Some tools keep the header; accept both.
        foreach (var zlib in new[] { false, true })
        {
            try
            {
                using var input = new MemoryStream(data);
                using Stream z = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(expected);
                var buffer = new byte[81920];
                int n;
                while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    if (output.Length > expected) throw new InvalidDataException("image data is larger than the image");
                }
                if (output.Length == expected) return output.ToArray();
            }
            catch (InvalidDataException) when (!zlib) { }
        }
        throw new InvalidDataException("the image data does not inflate to the image's size");
    }

    private static byte[] Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        var outp = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var cur = outp.AsSpan(y * stride, stride);
            var prev = y > 0 ? outp.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0, b = y > 0 ? prev[x] : 0, c = x >= bpp && y > 0 ? prev[x - bpp] : 0;
                cur[x] = (byte)(src[x] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"unknown PNG filter {filter} in row {y}"),
                });
            }
        }
        return outp;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Encode(int w, int h, byte color, byte[] pixels, int stride)
    {
        using var raw = new MemoryStream((stride + 1) * h);
        for (var y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(pixels, y * stride, stride); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.GetBuffer(), 0, (int)raw.Length);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8; ihdr[9] = color; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", z.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);
        s.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typeAndData));
        s.Write(len);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
