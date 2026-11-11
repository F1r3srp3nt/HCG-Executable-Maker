namespace HCG.ExecutableMaker.Account;

/// <summary>
/// The registration handoff, derived from the implemented flow: the client registration request (RFC 7591 metadata:
/// public native client, authorization code + PKCE S256, the exact loopback callbacks, the requested scopes), a
/// non-secret hcg-connect.json template the loader accepts, and the steps for HCG and the user. Never a client ID,
/// secret, endpoint or permission that does not exist in this build.
/// </summary>
public static class HcgRegistrationExport
{
    public const string RequestFile = "hcg-executable-maker-registration.json";
    public const string TemplateFile = "hcg-connect.template.json";
    public const string ReadmeFile = "HCG-CONNECT-SETUP.txt";

    public static IReadOnlyList<string> Write(string folder, HcgConnectConfiguration config)
    {
        Directory.CreateDirectory(folder);
        var request = Path.Combine(folder, RequestFile);
        var template = Path.Combine(folder, TemplateFile);
        var readme = Path.Combine(folder, ReadmeFile);
        File.WriteAllText(request, config.RegistrationRequestJson() + "\n");
        File.WriteAllText(template, HcgConnectConfiguration.TemplateJson());
        File.WriteAllText(readme, Readme(config));
        return [request, template, readme];
    }

    public static string Readme(HcgConnectConfiguration c) => $"""
        HCG Executable Maker — HCG Connect setup
        ========================================

        Executable Maker signs in as a PUBLIC native client: system browser, authorization code + PKCE S256, loopback
        callback. It holds no client secret and never asks for an HCG password.

        For HCG (central registration, ERP › App integrations):
        1. Register ONE public client for product "{c.ProductId}" with the metadata in {RequestFile}:
           token_endpoint_auth_method "none" (no secret), grant types authorization_code + refresh_token, PKCE S256,
           scopes "{string.Join(' ', c.Scopes)}", and exactly these redirect URIs:
        {string.Join("\n", c.RedirectUris.Select(u => "             " + u))}
           Do not reuse another app's client (for example HCG AI's website client).
        2. Put the issued client ID (a UUID) into {TemplateFile} in place of "{HcgConnectConfiguration.ClientIdPlaceholder}",
           save it as {HcgConnectConfiguration.FileName} and give it to Executable Maker users. It contains no secret.
        3. Grant the account features centrally (support.submit, support.attach, ai.use) as HCG decides; the app never
           grants them itself.

        For each user:
        1. Account › HCG Connect › Import HCG connection configuration… (choose the {HcgConnectConfiguration.FileName} from HCG).
        2. Check configuration: Configuration, Identity server, Signing keys and Client registration must show Passed.
           "Client registration: Failed" with oauth_client_not_found is HCG's own answer that it does not know the client ID.
        3. Sign in with HCG (your browser opens HCG's sign-in page), then restart the app once: it must still be signed in.

        Local builds, packaging and toolchains never need an HCG account.
        """;
}
