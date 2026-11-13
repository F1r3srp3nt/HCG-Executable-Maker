using System.Net;
using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using AppClass = HCG.ExecutableMaker.App.App;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// The Account page signed in, against the MOCK HCG (tools/hcg-mock/mock-hcg.mjs, never HCG): sign-in through the
/// browser flow, plan and access from the (mock) central API, organisation choice, an expired licence, and a checkout
/// whose return proves nothing until HCG reports the new plan.
/// Usage: hcg-ui-driver --account-signed-in --hcg-mock &lt;mock.json&gt; [--import-config] --output DIR --screenshots DIR --prefix account
/// --import-config: start unconfigured and import the issued configuration through the Account page (a placeholder file
/// is refused first), then run the connection check, before signing in.
/// </summary>
internal static partial class Program
{
    private static int _mockPort;
    private static readonly List<string> OpenedLinks = [];
    private static readonly HttpClient Local = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });

    private static void PrepareMockHcg(string dataRoot)
    {
        using var info = JsonDocument.Parse(File.ReadAllText(_args["hcg-mock"]));
        var r = info.RootElement;
        _mockPort = r.GetProperty("port").GetInt32();
        var issuer = r.GetProperty("issuer").GetString()!;
        Directory.CreateDirectory(dataRoot);
        // With --import-config the file is "issued" next to the data folder and imported through the Account page.
        var target = _args.ContainsKey("import-config") ? Path.Combine(Path.GetDirectoryName(dataRoot)!, "issued-hcg-connect.json") : Path.Combine(dataRoot, HcgConnectConfiguration.FileName);
        if (_args.ContainsKey("import-config")) _args["pick-file"] = target;
        File.WriteAllText(target, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contractVersion"] = HcgConnectConfiguration.ContractVersion, ["clientId"] = r.GetProperty("clientId").GetString()!, ["issuer"] = issuer,
            ["authorizationEndpoint"] = issuer + "/oauth/authorize", ["tokenEndpoint"] = issuer + "/oauth/token",
            ["userinfoEndpoint"] = issuer + "/oauth/userinfo", ["jwksEndpoint"] = issuer + "/.well-known/jwks.json",
            ["apiBaseUrl"] = r.GetProperty("apiBaseUrl").GetString()!,
        }));
    }

    /// <summary>The scripted "browser": follows a local test server's redirects (as after consent); HCG links are only recorded.</summary>
    private static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            OpenedLinks.Add(url);
            Log("Browser would open: " + url);
            return true;
        }
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback) return false;
        _ = Task.Run(async () =>
        {
            Uri? next = uri;
            for (var hop = 0; hop < 5 && next is not null; hop++)
            {
                using var response = await Local.GetAsync(next);
                if (response.StatusCode != HttpStatusCode.Found) return;
                next = response.Headers.Location is { } loc ? new Uri(next, loc) : null;
            }
        });
        return true;
    }

    private static async Task Scenario(object change)
    {
        using var response = await Local.PostAsync($"http://127.0.0.1:{_mockPort}/__mock/scenario",
            new StringContent(JsonSerializer.Serialize(change), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Account page › HCG Connect: refuse the placeholder example, import the issued file, check the connection.</summary>
    private static async Task ImportConfigurationAsync(AccountViewModel account)
    {
        Log($"Account: [{account.AccountTag.Text}] [{account.ConnectTag.Text}] {account.AccountMessage}");
        Check(account.IsNotConfigured && !account.CanSignIn, "Unconfigured build: sign-in is not offered and the reason is shown");
        Check(account.ConfigIssues.Any(i => i.Field == "clientId") && !account.AccountMessage.Contains("not registered", StringComparison.OrdinalIgnoreCase),
            "The missing field is named (clientId) and nothing is claimed about HCG's registration");
        account.CheckConnectionCommand.Execute(null);
        await WaitUntil(() => !account.IsCheckingConnection && account.ConnectionSteps.Count > 0, TimeSpan.FromSeconds(30), "connection check (unconfigured)");
        Check(account.ConnectionSteps[0].State.Text == "Failed" && account.ConnectionSteps.Skip(1).All(s => s.State.Text == "Not checked"),
            "Unconfigured: the check names the missing client ID and contacts nothing");
        Shot("connect-not-configured");

        var issued = _args["pick-file"];
        var example = Path.Combine(Path.GetDirectoryName(issued)!, "placeholder-hcg-connect.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "hcg-connect.example.json"), example, overwrite: true);
        _args["pick-file"] = example;
        await account.ImportConfigurationCommand.ExecuteAsync(null);
        Log("Import placeholder: " + account.ConnectImportError);
        Check(account.ConnectImportError?.Contains("not an issued HCG client ID", StringComparison.Ordinal) == true && account.IsNotConfigured,
            "The placeholder example is refused with the reason; nothing changes");
        Shot("connect-placeholder-refused");

        // A configuration with a client ID HCG does not know: only HCG's own answer marks it as rejected.
        var unknown = Path.Combine(Path.GetDirectoryName(issued)!, "unknown-client-hcg-connect.json");
        File.WriteAllText(unknown, File.ReadAllText(issued).Replace(JsonDocument.Parse(File.ReadAllText(issued)).RootElement.GetProperty("clientId").GetString()!, "99999999-9999-4999-8999-999999999999", StringComparison.Ordinal));
        _args["pick-file"] = unknown;
        await account.ImportConfigurationCommand.ExecuteAsync(null);
        Check(account.ConnectImportError is null && !account.IsNotConfigured, "A well-formed configuration is imported (not yet checked with HCG)");
        account.CheckConnectionCommand.Execute(null);
        await WaitUntil(() => !account.IsCheckingConnection && account.ConnectionSteps.Count == 6, TimeSpan.FromSeconds(60), "connection check (unknown client)");
        Log($"Account: [{account.AccountTag.Text}] {account.AccountMessage}");
        Check(account.IsClientRejected && account.AccountTag.Text == "Rejected by HCG" && account.AccountMessage.Contains("99999999-9999-4999-8999-999999999999", StringComparison.Ordinal),
            "An unknown client ID is reported as rejected by HCG, from the identity service's answer");
        Shot("connect-rejected-by-hcg");

        _args["pick-file"] = issued;
        await account.ImportConfigurationCommand.ExecuteAsync(null);
        Log("Import issued: " + (account.ConnectImportMessage ?? account.ConnectImportError));
        Check(account.ConnectImportError is null && !account.IsNotConfigured && account.CanSignIn, "The issued configuration is imported and in use without a restart");
        account.CheckConnectionCommand.Execute(null);
        await WaitUntil(() => !account.IsCheckingConnection && account.ConnectionSteps.Count == 6, TimeSpan.FromSeconds(60), "connection check");
        foreach (var step in account.ConnectionSteps) Log($"Check {step.Title}: [{step.State.Text}] {step.Detail}");
        Check(account.ConnectionReady, "Connection check: identity server, keys and client registration pass (sign-in not yet proven)");
        Check(!account.IsClientRejected && account.CanSignIn, "The rejection is cleared once HCG accepts the client");
        await account.RetryConnectionCommand.ExecuteAsync(null);
        Check(account.CanSignIn && account.ConfigIssues.Count == 0, "Retry connection re-reads the saved configuration (persisted in the data folder)");
        Shot("connect-checked");
    }

    private static async Task<int> RunAccountSignedInAsync(MainWindowViewModel main)
    {
        var plan = AppClass.Services!.GetRequiredService<AccountPlanService>();
        await main.NavigateAsync("account", null);
        var account = (AccountViewModel)main.CurrentPage!;
        await Task.Delay(300);
        if (_args.ContainsKey("import-config")) await ImportConfigurationAsync(account);
        Log($"Account: [{account.AccountTag.Text}] {account.AccountHeadline} — {account.AccountMessage}");
        Check(!account.IsNotConfigured && account.CanSignIn, "Sign in with HCG is offered (client ID configured)");
        Check(account.UsesLocalIdentityServer, "The page says sign-in points at a local test server, not HCG");
        Shot("signed-out");

        account.SignInCommand.Execute(null);
        await WaitUntil(() => account.IsSignedIn && account.PlanTag.Text == "Active", TimeSpan.FromSeconds(60), "signed in with plan");
        Log($"Account: [{account.AccountTag.Text}] {account.AccountMessage}");
        Log($"Plan: [{account.PlanTag.Text}] {account.PlanName} · valid until {account.ValidUntil} · {account.PlanMessage}");
        foreach (var f in account.Features) Log($"Feature: {f.Title} [{f.State.Text}]");
        Check(account.Email == "founder@example.test", "Signed in through the browser flow (PKCE S256, state, nonce, ES256 ID token)");
        Check(account.PlanName == "Pro" && account.ValidUntil.Length > 0, "Plan and validity come from the central API");
        Check(account.HasManageLink && !account.HasCheckoutLink, "Only the links HCG provides are offered (manage; no checkout)");
        Shot("plan-active");

        await Scenario(new { organisations = new[] { new { org_id = "org-hcg", name = "Hakmi Capital Group", role = "owner" }, new { org_id = "org-client", name = "Client Co AS", role = "member" } } });
        account.CheckPlanCommand.Execute(null);
        await WaitUntil(() => account.NeedsOrganisation, TimeSpan.FromSeconds(20), "organisation choice");
        Check(account.HasOrganisations && account.PlanTag.Text == "Choose organisation", "Several organisations: the user chooses; nothing is assumed");
        Shot("choose-organisation");
        account.SelectedOrganisation = account.Organisations.Single(o => o.Id == "org-client");
        await WaitUntil(() => account.PlanTag.Text == "Active", TimeSpan.FromSeconds(20), "organisation applied");
        Check(plan.OrganisationId == "org-client", "The chosen organisation is sent to HCG");
        Shot("organisation-chosen");

        await Scenario(new { licence = new { status = "expired", license_type = "subscription", starts_at = "2025-11-01T00:00:00Z", ends_at = "2026-11-01T00:00:00Z" } });
        account.CheckPlanCommand.Execute(null);
        await WaitUntil(() => account.PlanTag.Text == "Expired", TimeSpan.FromSeconds(20), "expired licence");
        Log($"Plan: [{account.PlanTag.Text}] {account.PlanMessage}");
        Check(account.Features.All(f => f.State.Text != "Allowed"), "An expired licence allows nothing");
        Shot("licence-expired");

        await Scenario(new
        {
            plan = new { plan_key = "free", name = "Free", billing_type = "free" },
            licence = new { status = "active", license_type = "free", starts_at = "2026-11-01T00:00:00Z", ends_at = (string?)null },
            links = new { manage = "https://beta.hakmicapitalgroup.com/client-portal", checkout = "https://beta.hakmicapitalgroup.com/pricing", account = "https://beta.hakmicapitalgroup.com/client-portal" },
        });
        account.CheckPlanCommand.Execute(null);
        await WaitUntil(() => account.PlanName == "Free" && account.HasCheckoutLink, TimeSpan.FromSeconds(20), "free plan with checkout link");
        account.OpenCheckoutCommand.Execute(null);
        await WaitUntil(() => account.HasChange, TimeSpan.FromSeconds(10), "awaiting confirmation");
        Check(OpenedLinks.LastOrDefault() == "https://beta.hakmicapitalgroup.com/pricing", "The checkout link HCG provided was opened in the browser");
        Check(plan.ChangeState == PlanChangeState.AwaitingConfirmation, "The app waits for HCG to confirm");
        Shot("checkout-awaiting-confirmation");

        await plan.OnReturnedAsync(); // the user comes back; HCG still reports Free
        await Task.Delay(300);
        Check(account.PlanName == "Free" && plan.ChangeState == PlanChangeState.AwaitingConfirmation, "Returning from checkout changes nothing by itself");
        await Scenario(new { plan = new { plan_key = "pro", name = "Pro", billing_type = "consumer_subscription" },
            licence = new { status = "active", license_type = "subscription", starts_at = "2026-11-09T00:00:00Z", ends_at = "2027-11-09T00:00:00Z" } });
        await plan.OnReturnedAsync();
        await WaitUntil(() => account.PlanName == "Pro" && plan.ChangeState == PlanChangeState.Confirmed, TimeSpan.FromSeconds(20), "HCG confirmation");
        Log("Change: " + account.ChangeMessage);
        Check(true, "The plan changed only after HCG's API reported it");
        Shot("checkout-confirmed-by-hcg");

        account.SignOutCommand.Execute(null);
        await WaitUntil(() => !account.IsSignedIn, TimeSpan.FromSeconds(10), "signed out");
        Check(account.PlanName == "—" && account.Features.Count == 0, "Signing out clears plan and access");
        Shot("signed-out-again");
        return 0;
    }
}
