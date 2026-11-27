using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Test-only ad hoc code signing with real CodeDirectories (page hashes over the binary, special slots bound to
/// Info.plist, requirements, CodeResources and entitlements), in the two forms found in jailbreak packages:
/// <list type="bullet">
/// <item><b>codesign style</b> (Xcode, or prebuilt frameworks such as SkiaSharp's): one SHA-256 directory, the ad hoc flag,
/// an empty CMS blob;</item>
/// <item><b>ldid style</b>: a SHA-1 directory with a SHA-256 alternate, no flags, no CMS, entitlements in XML and DER slots.</item>
/// </list>
/// The binary must already have an LC_CODE_SIGNATURE (as the linker reserves it); the signature replaces whatever is at
/// its offset. Nothing here is Apple's signing: it only produces the same structures so the validator can be tested.
/// </summary>
internal static class AdHocSigner
{
    public const uint AdHoc = 0x2, Runtime = 0x10000, LibraryValidation = 0x200;

    public static byte[] Sign(byte[] macho, string identifier, bool ldidStyle = false, uint? flags = null, byte[]? infoPlist = null,
        byte[]? codeResources = null, string? entitlementsXml = null, bool mainBinary = false, string? teamId = null)
    {
        var (lcOff, dataOff) = FindSignatureCommand(macho);
        var code = macho.AsSpan(0, dataOff).ToArray();
        // As codesign and ldid do: the load command gets the signature's final size before any page is hashed.
        var size = Build(code, identifier, ldidStyle, flags, infoPlist, codeResources, entitlementsXml, mainBinary, teamId).Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(lcOff + 12), (uint)size);
        var sig = Build(code, identifier, ldidStyle, flags, infoPlist, codeResources, entitlementsXml, mainBinary, teamId);
        var result = new byte[dataOff + sig.Length];
        code.CopyTo(result, 0);
        sig.CopyTo(result, dataOff);
        return result;
    }

    private static byte[] Build(byte[] code, string identifier, bool ldidStyle, uint? flags, byte[]? infoPlist, byte[]? codeResources, string? entitlementsXml, bool mainBinary, string? teamId)
    {
        var requirements = Blob(0xFADE0C01, new byte[4]);                       // empty requirement set (count 0)
        byte[]? ents = entitlementsXml is null ? null : Blob(0xFADE7171, Encoding.UTF8.GetBytes(entitlementsXml));
        byte[]? der = entitlementsXml is null ? null : Blob(0xFADE7172, Encoding.UTF8.GetBytes("DER:" + entitlementsXml));   // bound by hash only
        var specials = new Dictionary<int, byte[]?> { [1] = infoPlist, [2] = requirements, [3] = codeResources, [5] = ents, [7] = der };
        var blobs = new List<(uint Slot, byte[] Data)>();
        var f = flags ?? (ldidStyle ? 0u : AdHoc);
        if (ldidStyle)
        {
            blobs.Add((0, CodeDirectory(code, 1, identifier, f, specials, mainBinary, teamId)));
            blobs.Add((2, requirements));
            if (ents is not null) blobs.Add((5, ents));
            if (der is not null) blobs.Add((7, der));
            blobs.Add((0x1000, CodeDirectory(code, 2, identifier, f, specials, mainBinary, teamId)));
        }
        else
        {
            blobs.Add((0, CodeDirectory(code, 2, identifier, f, specials, mainBinary, teamId)));
            blobs.Add((2, requirements));
            if (ents is not null) blobs.Add((5, ents));
            if (der is not null) blobs.Add((7, der));
            blobs.Add((0x10000, Blob(0xFADE0B01, [])));                          // empty CMS: ad hoc
        }
        var header = 12 + 8 * blobs.Count;
        var sig = new byte[header + blobs.Sum(b => b.Data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(sig, 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(sig.AsSpan(4), (uint)sig.Length);
        BinaryPrimitives.WriteUInt32BigEndian(sig.AsSpan(8), (uint)blobs.Count);
        var at = header;
        for (var i = 0; i < blobs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sig.AsSpan(12 + 8 * i), blobs[i].Slot);
            BinaryPrimitives.WriteUInt32BigEndian(sig.AsSpan(16 + 8 * i), (uint)at);
            blobs[i].Data.CopyTo(sig, at);
            at += blobs[i].Data.Length;
        }
        return sig;
    }

    private static byte[] Blob(uint magic, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, magic);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)b.Length);
        payload.CopyTo(b, 8);
        return b;
    }

    private static (int LcOffset, int DataOffset) FindSignatureCommand(byte[] m)
    {
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16));
        var off = 32;
        for (var i = 0; i < ncmds; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(off));
            if (cmd == 0x1D) return (off, (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(off + 8)));
            off += (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(off + 4));
        }
        throw new InvalidOperationException("no LC_CODE_SIGNATURE: build the fixture with signed: true");
    }

    private static byte[] CodeDirectory(byte[] code, byte hashType, string identifier, uint flags, Dictionary<int, byte[]?> specials, bool mainBinary, string? teamId)
    {
        const int page = 4096, headerSize = 0x58;
        int hashSize = hashType == 1 ? 20 : 32;
        byte[] H(ReadOnlySpan<byte> x) => hashType == 1 ? SHA1.HashData(x) : SHA256.HashData(x);
        var nCode = (code.Length + page - 1) / page;
        const int nSpecial = 7;
        var ident = Encoding.UTF8.GetBytes(identifier + "\0");
        var team = teamId is null ? [] : Encoding.UTF8.GetBytes(teamId + "\0");
        var identOffset = headerSize;
        var teamOffset = teamId is null ? 0 : identOffset + ident.Length;
        var hashOffset = identOffset + ident.Length + team.Length + nSpecial * hashSize;
        var cd = new byte[hashOffset + nCode * hashSize];
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(cd.AsSpan(o), v);
        U32(0, 0xFADE0C02); U32(4, (uint)cd.Length); U32(8, 0x20400); U32(12, flags);
        U32(16, (uint)hashOffset); U32(20, (uint)identOffset); U32(24, nSpecial); U32(28, (uint)nCode); U32(32, (uint)code.Length);
        cd[36] = (byte)hashSize; cd[37] = hashType; cd[38] = 0; cd[39] = 12;
        U32(48, (uint)teamOffset);
        BinaryPrimitives.WriteUInt64BigEndian(cd.AsSpan(0x48), (ulong)Math.Min(code.Length, page));     // exec segment limit
        BinaryPrimitives.WriteUInt64BigEndian(cd.AsSpan(0x50), mainBinary ? 1UL : 0UL);                 // CS_EXECSEG_MAIN_BINARY
        ident.CopyTo(cd, identOffset);
        team.CopyTo(cd, identOffset + ident.Length);
        foreach (var (slot, data) in specials)
            if (data is not null) H(data).AsSpan(0, hashSize).CopyTo(cd.AsSpan(hashOffset - slot * hashSize));
        for (var i = 0; i < nCode; i++)
            H(code.AsSpan(i * page, Math.Min(page, code.Length - i * page))).AsSpan(0, hashSize).CopyTo(cd.AsSpan(hashOffset + i * hashSize));
        return cd;
    }
}
