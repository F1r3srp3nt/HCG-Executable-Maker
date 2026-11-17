using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// docs/hcg-connect/hcg-connect.schema.json and hcg-connect.example.json are the handoff's contract: they must describe
/// exactly what the app accepts, and the example must carry the built-in (live-verified) values.
/// </summary>
public class HcgConnectConfigurationTests
{
    private static string Doc(string name) => Path.Combine(Fixtures.RepoRoot, "docs", "hcg-connect", name);
    private const string IssuedId = "3f0c2b9e-7a41-4d8e-9b25-6c1e8f4a2d70";

    private static HcgConnectConfiguration LoadExample(string? clientId = null)
    {
        using var t = new TempDir();
        var text = File.ReadAllText(Doc("hcg-connect.example.json"));
        if (clientId is not null) text = text.Replace("<ISSUED_PUBLIC_CLIENT_ID>", clientId);
        t.Write(HcgConnectConfiguration.FileName, text);
        var r = HcgConnectConfiguration.Load(t.Path);
        Assert.Null(r.Problem);
        return r.Configuration;
    }

    [Fact]
    public void The_example_with_its_placeholder_is_accepted_but_sign_in_stays_off()
    {
        var c = LoadExample();
        Assert.Equal("<ISSUED_PUBLIC_CLIENT_ID>", c.ClientId);
        Assert.Contains(c.MissingForSignIn(), m => m.Contains("not an issued HCG client ID"));
    }

    [Fact]
    public void The_example_with_an_issued_id_is_ready_and_otherwise_equals_the_built_in_defaults()
    {
        var c = LoadExample(IssuedId);
        Assert.Empty(c.MissingForSignIn());
        Assert.False(c.UsesLocalIdentityServer);
        var expected = HcgConnectConfiguration.Default with { ClientId = IssuedId };
        Assert.Equal(expected.ToJson(), c.ToJson());
    }

    [Fact]
    public void The_schema_lists_exactly_the_keys_the_app_accepts_and_its_defaults_are_the_built_in_values()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Doc("hcg-connect.schema.json")))!;
        var props = schema["properties"]!.AsObject();
        Assert.Equal(HcgConnectConfiguration.KnownKeys.OrderBy(k => k, StringComparer.Ordinal),
            props.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(HcgConnectConfiguration.ContractVersion, (string?)props["contractVersion"]!["const"]);
        Assert.False((bool)schema["additionalProperties"]!);

        var defaults = JsonNode.Parse(HcgConnectConfiguration.Default.ToJson())!.AsObject();
        foreach (var (key, node) in props)
        {
            if (node!["default"] is not { } d) continue;
            Assert.True(defaults.ContainsKey(key), key);
            Assert.True(JsonNode.DeepEquals(d, defaults[key]), $"{key}: schema default {d.ToJsonString()} ≠ app default {defaults[key]?.ToJsonString()}");
        }
    }

    [Theory]
    [InlineData("""{"contractVersion":"hcg.executable-maker.connect/1","clientId":"x","clientID":"y"}""", "unknown setting \"clientID\"")]
    [InlineData("""{"contractVersion":"hcg.executable-maker.connect/1","clientId":"x","clientSecret":"y"}""", "must not contain secrets")]
    [InlineData("""{"contractVersion":"hcg.executable-maker.connect/1","clientId":"x","apiKey":"y"}""", "must not contain secrets")]
    [InlineData("""["not","an","object"]""", "must be a JSON object")]
    public void Typos_secrets_and_wrong_shapes_make_the_app_ignore_the_file(string json, string problem)
    {
        using var t = new TempDir();
        t.Write(HcgConnectConfiguration.FileName, json);
        var r = HcgConnectConfiguration.Load(t.Path, HcgEnvironment.Development);
        Assert.Contains(problem, r.Problem);
        Assert.Null(r.Configuration.ClientId); // ignored: the built-in registration (development: none issued) applies
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("hcg-executable-maker")]
    [InlineData("3f0c2b9e7a414d8e9b256c1e8f4a2d70")]
    public void Only_a_real_issued_uuid_turns_sign_in_on(string clientId) =>
        Assert.Contains((HcgConnectConfiguration.Default with { ClientId = clientId }).MissingForSignIn(), m => m.Contains("not an issued HCG client ID"));

    [Fact]
    public void Scopes_must_include_openid_and_default_to_the_minimal_identity_scopes()
    {
        Assert.Equal(["openid", "email", "profile"], HcgConnectConfiguration.Default.Scopes);
        var c = HcgConnectConfiguration.Default with { ClientId = IssuedId, Scopes = ["email"] };
        Assert.Contains(c.MissingForSignIn(), m => m.Contains("openid"));
    }

    [Fact]
    public void Identity_must_be_https_except_a_test_server_on_this_computer()
    {
        var local = HcgConnectConfiguration.Default with
        {
            ClientId = IssuedId, Issuer = new("http://127.0.0.1:5999/auth/v1"), AuthorizationEndpoint = new("http://127.0.0.1:5999/auth/v1/oauth/authorize"),
            TokenEndpoint = new("http://127.0.0.1:5999/auth/v1/oauth/token"), UserinfoEndpoint = new("http://127.0.0.1:5999/auth/v1/oauth/userinfo"),
            JwksEndpoint = new("http://127.0.0.1:5999/auth/v1/.well-known/jwks.json"),
        };
        Assert.Empty(local.MissingForSignIn());
        Assert.True(local.UsesLocalIdentityServer);
        var remote = local with { TokenEndpoint = new("http://evil.example/token") };
        Assert.Contains(remote.MissingForSignIn(), m => m.Contains("HTTPS"));
    }

    [Theory]
    [InlineData("https://beta.hakmicapitalgroup.com/client-portal", true)]
    [InlineData("https://hakmicapitalgroup.com/pricing", true)]
    [InlineData("http://beta.hakmicapitalgroup.com/client-portal", false)]
    [InlineData("https://beta.hakmicapitalgroup.com.evil.example/x", false)]
    [InlineData("https://user:pw@beta.hakmicapitalgroup.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Only_https_links_on_hcg_hosts_are_offered(string? url, bool offered) =>
        Assert.Equal(offered, HcgConnectConfiguration.Default.TrustedLink(url) is not null);

    // ───────────── Environment-specific registration (HCG's main site, 13 Nov 2026) ─────────────

    /// <summary>The hcg-connect.json HCG's Owner screen exports for the production registration (main site
    /// docs/executable-maker/hcg-connect.json), byte for byte except whitespace.</summary>
    private const string OwnerExport = """
        {"contractVersion":"hcg.executable-maker.connect/1","clientId":"fb9b6edf-2bfc-4b5e-a354-dbb1af697029",
         "issuer":"https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1",
         "authorizationEndpoint":"https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/authorize",
         "tokenEndpoint":"https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/token",
         "userinfoEndpoint":"https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/oauth/userinfo",
         "jwksEndpoint":"https://qggdyzoydveafkmjgkpq.supabase.co/auth/v1/.well-known/jwks.json",
         "redirectUris":["http://127.0.0.1:47820/oauth/callback","http://127.0.0.1:47821/oauth/callback","http://127.0.0.1:47822/oauth/callback"],
         "scopes":["openid","email","profile"],"apiBaseUrl":"https://beta.hakmicapitalgroup.com","ecosystemApiPath":"/api/public/ecosystem/v1",
         "productId":"hcg.executable.maker","accountPortalUrl":"https://beta.hakmicapitalgroup.com/client-portal",
         "trustedLinkHosts":["beta.hakmicapitalgroup.com","hakmicapitalgroup.com"]}
        """;

    [Fact]
    public void The_production_registration_hcg_issued_is_built_in_and_identical_to_hcgs_export()
    {
        var p = HcgConnectConfiguration.Production;
        Assert.Equal("fb9b6edf-2bfc-4b5e-a354-dbb1af697029", p.ClientId);
        Assert.Equal("hcg-executable-maker-desktop-production", p.RegistryKey);
        Assert.Empty(p.MissingForSignIn());
        var (exported, problem) = HcgConnectConfiguration.Parse(OwnerExport, HcgEnvironment.Production);
        Assert.Null(problem);
        Assert.Equal(p.ToJson(), exported.ToJson()); // every value HCG exported is the built-in default
    }

    [Fact]
    public void The_development_registration_has_no_issued_client_and_never_borrows_production()
    {
        var d = HcgConnectConfiguration.Development;
        Assert.Null(d.ClientId);
        Assert.Equal("hcg-executable-maker-desktop", d.RegistryKey);
        Assert.Contains("development registration", Assert.Single(d.Issues()).Problem);
        using var t = new TempDir();
        Assert.Null(HcgConnectConfiguration.Load(t.Path, HcgEnvironment.Development).Configuration.ClientId);
        Assert.Equal(HcgConnectConfiguration.ProductionClientId, HcgConnectConfiguration.Load(t.Path, HcgEnvironment.Production).Configuration.ClientId);
    }

    [Fact]
    public void Each_registration_keeps_its_own_stored_session()
    {
        var local = HcgConnectConfiguration.Production with { Issuer = new Uri("http://127.0.0.1:9/auth/v1") };
        Assert.Equal(["session", "session-development", "session-local-test"],
            new[] { HcgConnectConfiguration.Production.SessionStoreAccount, HcgConnectConfiguration.Development.SessionStoreAccount, local.SessionStoreAccount });
        Assert.Equal(HcgEnvironment.Development, HcgConnectConfiguration.Parse(OwnerExport, HcgEnvironment.Development).Configuration.Environment);
    }

    [Theory]
    [InlineData("development", HcgEnvironment.Development)]
    [InlineData(" Development ", HcgEnvironment.Development)]
    [InlineData("production", HcgEnvironment.Production)]
    [InlineData(null, HcgEnvironment.Production)]
    [InlineData("staging", HcgEnvironment.Production)] // no such registration: production, never a guessed third one
    public void The_environment_variable_selects_the_registration(string? value, HcgEnvironment expected) =>
        Assert.Equal(expected, HcgConnectConfiguration.ParseEnvironment(value));
}
