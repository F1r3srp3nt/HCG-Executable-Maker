using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace HCG.ExecutableMaker.Packaging.Verification;

/// <summary>
/// Reads the header of an Electron app.asar archive (Chromium pickle: [4][header size][payload size][json length][json])
/// to check which files the packaged app contains, and reads small files from it. Read-only; nothing is extracted to disk.
/// </summary>
public static class Asar
{
    public sealed class Archive(string path, JsonElement files, long dataOffset)
    {
        public string Path { get; } = path;

        /// <summary>The entry for a '/'-separated path, following directories ("files"); null when absent.</summary>
        public JsonElement? Find(string relative)
        {
            var node = files;
            foreach (var part in relative.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out var child)) return null;
                if (child.TryGetProperty("files", out var sub)) node = sub;
                else return child;
            }
            return node;
        }

        public bool Contains(string relative) => Find(relative) is not null;

        public bool IsUnpacked(string relative) => Find(relative) is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("unpacked", out var u) && u.ValueKind == JsonValueKind.True;

        /// <summary>Contents of a file stored inside the archive (not for unpacked files); null when absent or too large.</summary>
        public byte[]? ReadFile(string relative, int maxBytes = 4 * 1024 * 1024)
        {
            if (Find(relative) is not { } e || !e.TryGetProperty("size", out var sizeEl) || !e.TryGetProperty("offset", out var offEl)) return null;
            var size = sizeEl.GetInt64();
            if (size > maxBytes || !long.TryParse(offEl.GetString(), out var offset)) return null;
            using var fs = File.OpenRead(Path);
            fs.Seek(dataOffset + offset, SeekOrigin.Begin);
            var buffer = new byte[size];
            fs.ReadExactly(buffer);
            return buffer;
        }
    }

    public static Archive Open(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[16];
        fs.ReadExactly(head);
        var sizePickle = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        if (sizePickle != 4 || jsonLength == 0 || jsonLength > 64 * 1024 * 1024 || jsonLength > headerSize)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not an asar archive.");
        var json = new byte[jsonLength];
        fs.ReadExactly(json);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        if (!doc.RootElement.TryGetProperty("files", out var files)) throw new InvalidDataException("The asar header has no file list.");
        return new Archive(path, files.Clone(), 8 + headerSize);
    }
}
