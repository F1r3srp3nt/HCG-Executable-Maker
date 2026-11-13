using System.Security.Cryptography;
using System.Text;

namespace HCG.ExecutableMaker.Account;

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url length.") };
        return Convert.FromBase64String(s);
    }

    /// <summary>A cryptographically random URL-safe token.</summary>
    public static string RandomToken(int bytes = 32) => Encode(RandomNumberGenerator.GetBytes(bytes));
}

/// <summary>RFC 7636 proof key: a random verifier (43–128 chars) and its S256 challenge.</summary>
public sealed record PkcePair(string Verifier, string Challenge)
{
    public static PkcePair Create()
    {
        var verifier = Base64Url.RandomToken(48); // 64 characters
        return new PkcePair(verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) => Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
