using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Signing;

/// <summary>
/// Integration boundary for App Store Connect API keys (.p8, Key ID, Issuer ID) — kept apart from signing identities.
/// Per Apple's documentation ("Creating API Keys for App Store Connect API", "Generating Tokens for API Requests"):
/// team keys are created by an Admin with a role and use the Issuer ID as the JWT <c>iss</c> claim; individual keys
/// carry their user's access, use <c>sub: "user"</c> and no Issuer ID, and cannot use Provisioning endpoints or
/// notarytool. The private key can be downloaded once; Apple keeps no copy. An API key authorises API requests (ES256
/// JWTs); it does not sign apps. HCG does not store .p8 contents and does not require a key for local signing.
/// </summary>
public static class AppStoreConnectKeys
{
    public const string Explanation =
        "An App Store Connect API key authorises App Store Connect API requests (and, for team keys, notarytool). It is not an app-signing certificate, " +
        "it is not needed to sign locally, and HCG does not upload anything with it automatically.";

    public static IReadOnlyList<string> Validate(AppStoreConnectApiKeyReference key)
    {
        var problems = new List<string>();
        if (key.KeyId.Length != 10 || !key.KeyId.All(char.IsAsciiLetterOrDigit) || key.KeyId.Any(char.IsLower))
            problems.Add("The Key ID is 10 upper-case letters or digits (App Store Connect › Users and Access › Integrations).");
        switch (key.Type)
        {
            case AppStoreConnectKeyType.Team when key.IssuerId is null || !Guid.TryParse(key.IssuerId, out _):
                problems.Add("A team key needs the Issuer ID (a UUID shown at the top of the Integrations tab).");
                break;
            case AppStoreConnectKeyType.Individual when key.IssuerId is not null:
                problems.Add("Individual keys do not use an Issuer ID; leave it empty.");
                break;
        }
        if (key.PrivateKeyLocation is { } loc && loc.EndsWith(".p8", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(loc))
            problems.Add("Give the full path of the .p8 file (HCG stores only the path, never the key).");
        return problems;
    }

    /// <summary>What a key type can be used for, for display next to the reference.</summary>
    public static string Capabilities(AppStoreConnectKeyType type) => type switch
    {
        AppStoreConnectKeyType.Team => "Team key: access to all apps per its role; can be used with notarytool and the Provisioning endpoints.",
        _ => "Individual key: the access of its user; cannot use the Provisioning endpoints or notarytool.",
    };
}
