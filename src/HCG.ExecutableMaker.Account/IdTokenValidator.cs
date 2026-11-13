using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// Validates the OpenID Connect ID token the HCG identity server returns for the <c>openid</c> scope: JWS signature
/// against the issuer's JWKS (ES256 — the live key is P-256 — or RS256), <c>iss</c>, <c>aud</c> (this client),
/// <c>exp</c>, <c>iat</c>, <c>nonce</c> (bound to this sign-in) and <c>sub</c>. Any failure rejects the sign-in.
/// </summary>
public static class IdTokenValidator
{
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public sealed record Claims(string Subject, string? Email, string? Name, DateTimeOffset ExpiresAt, JsonElement Raw);

    public sealed class ValidationException(string message) : Exception(message);

    public static Claims Validate(string idToken, string jwksJson, string expectedIssuer, string clientId, string expectedNonce, DateTimeOffset now)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw new ValidationException("The ID token is not a signed JWT.");
        JsonElement header, payload;
        try
        {
            header = JsonDocument.Parse(Base64Url.Decode(parts[0])).RootElement.Clone();
            payload = JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ValidationException("The ID token could not be decoded.");
        }
        var alg = Str(header, "alg");
        var kid = Str(header, "kid");
        if (alg is not ("ES256" or "RS256")) throw new ValidationException($"The ID token uses an unsupported algorithm ({alg ?? "none"}).");
        if (!VerifySignature(alg, kid, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.Decode(parts[2]), jwksJson))
            throw new ValidationException("The ID token signature does not match the HCG identity server's keys.");

        if (Str(payload, "iss") != expectedIssuer.TrimEnd('/') && Str(payload, "iss") != expectedIssuer)
            throw new ValidationException("The ID token was issued by a different server.");
        var audOk = payload.TryGetProperty("aud", out var aud) && (aud.ValueKind == JsonValueKind.String ? aud.GetString() == clientId
            : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(a => a.GetString() == clientId));
        if (!audOk) throw new ValidationException("The ID token was issued for a different application.");
        if (!payload.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out var exp))
            throw new ValidationException("The ID token has no expiry.");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (expiresAt + ClockSkew < now) throw new ValidationException("The ID token has expired.");
        if (payload.TryGetProperty("iat", out var iatEl) && iatEl.TryGetInt64(out var iat) && DateTimeOffset.FromUnixTimeSeconds(iat) - ClockSkew > now)
            throw new ValidationException("The ID token is dated in the future.");
        if (Str(payload, "nonce") != expectedNonce) throw new ValidationException("The ID token does not belong to this sign-in (nonce mismatch).");
        var sub = Str(payload, "sub");
        if (string.IsNullOrWhiteSpace(sub)) throw new ValidationException("The ID token has no subject.");
        return new Claims(sub, Str(payload, "email"), Str(payload, "name") ?? Str(payload, "preferred_username"), expiresAt, payload);
    }

    private static bool VerifySignature(string alg, string? kid, byte[] data, byte[] signature, string jwksJson)
    {
        using var doc = JsonDocument.Parse(jwksJson);
        if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) return false;
        foreach (var key in keys.EnumerateArray())
        {
            if (kid is not null && Str(key, "kid") is { } k && k != kid) continue;
            if (Str(key, "use") is { } use && use != "sig") continue;
            try
            {
                if (alg == "ES256" && Str(key, "kty") == "EC" && Str(key, "crv") == "P-256")
                {
                    using var ec = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = Base64Url.Decode(Str(key, "x")!), Y = Base64Url.Decode(Str(key, "y")!) },
                    });
                    if (ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
                }
                else if (alg == "RS256" && Str(key, "kty") == "RSA")
                {
                    using var rsa = RSA.Create(new RSAParameters { Modulus = Base64Url.Decode(Str(key, "n")!), Exponent = Base64Url.Decode(Str(key, "e")!) });
                    if (rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return true;
                }
            }
            catch (CryptographicException) { }
            catch (FormatException) { }
        }
        return false;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Unverified claims of an access token (only for expiry scheduling and display; the server verifies it).</summary>
    public static JsonElement? PeekClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try { return JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement.Clone(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
