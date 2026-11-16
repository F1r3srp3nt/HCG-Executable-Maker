using System.Diagnostics;
using System.Net;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Core;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --hcg-session sign-in|status|sign-out</c>: the app's own HCG account services (the same ones the
/// Account page uses, from the same composition, configuration and OS credential store), without the UI.
/// <list type="bullet">
/// <item><c>status</c> restores the stored session like a normal start (one refresh) and prints the state, plan and access.</item>
/// <item><c>sign-in</c> signs in through the system browser. Only when hcg-connect.json points at a local test identity
/// server (http on 127.0.0.1/localhost, never HCG) does it follow the redirects itself, so CI can verify a packaged app.</item>
/// <item><c>sign-out</c> removes the stored session from this computer.</item>
/// <item><c>import &lt;file&gt;</c> validates an issued hcg-connect.json and installs it in the data folder (as the Account page does).</item>
/// <item><c>check</c> checks the configuration against the identity server and HCG's APIs without signing in.</item>
/// <item><c>registration</c> prints the client registration HCG needs, produced from this build's configuration.</item>
/// <item><c>features</c> prints the shared feature-access decisions (the same the Account page shows).</item>
/// <item><c>require &lt;feature&gt;</c> asks HCG now, as before a privileged operation; exit 0 allowed, 8 not permitted,
/// 9 HCG unavailable or not available yet, 3 sign-in needed.</item>
/// </list>
/// Never prints a token. Exit code 0 when the requested state was reached.
/// </summary>
internal static class HcgSessionCommand
{
    public static int Run(string[] args)
    {
        var verb = args.SkipWhile(a => a != "--hcg-session").Skip(1).FirstOrDefault() ?? "status";
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var account = services.GetRequiredService<HcgAccountService>();
        var plan = services.GetRequiredService<AccountPlanService>();
        Console.WriteLine(HostInfo.BuildIdentity);
        Console.WriteLine("Data folder: " + paths.Root);
        Console.WriteLine("Credential store: " + account.StoreDescription + (account.StoreNote is { } n ? " — " + n : ""));
        if (account.ConfigurationProblem is { } problem) Console.WriteLine("Configuration: " + problem);
        Console.WriteLine("Identity server: " + account.Configuration.Issuer + (account.Configuration.UsesLocalIdentityServer ? " (LOCAL TEST SERVER, not HCG)" : ""));
        Console.WriteLine(account.Configuration.UsesLocalIdentityServer
            ? $"Registration: local test identity server (not HCG) · client {account.Configuration.ClientId ?? "(none)"}"
            : $"Registration: {account.Configuration.Environment} ({account.Configuration.RegistryKey}) · client {account.Configuration.ClientId ?? "(none issued)"}");
        return verb switch
        {
            "status" => Status(account, plan),
            "sign-in" => SignIn(account, plan),
            "sign-out" => SignOut(account),
            "import" => Import(args.SkipWhile(a => a != "import").Skip(1).FirstOrDefault(), paths, account, services.GetRequiredService<HcgConnectionCheck>()),
            "check" => Check(account.Configuration, services.GetRequiredService<HcgConnectionCheck>()),
            "registration" => Registration(account.Configuration),
            "features" => Features(account, services.GetRequiredService<FeatureAccessService>(), args.Contains("--sign-in")),
            "require" => Require(args.SkipWhile(a => a != "require").Skip(1).FirstOrDefault(), account, services.GetRequiredService<FeatureAccessService>(), args.Contains("--sign-in")),
            "export" => Export(args.SkipWhile(a => a != "export").Skip(1).FirstOrDefault(), account.Configuration),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: HCGExecutableMaker --hcg-session status|sign-in|sign-out|check|registration|features|require <feature>|import <hcg-connect.json>|export <folder>");
        return 2;
    }

    private static int Import(string? file, IAppPaths paths, HcgAccountService account, HcgConnectionCheck check)
    {
        if (file is null) return Usage();
        var result = HcgConnectImport.Import(Path.GetFullPath(file), paths.Root);
        if (result.Error is not null) { Console.WriteLine("Import: " + result.Error); return 6; }
        account.ApplyConfigurationAsync(result.Configuration!).GetAwaiter().GetResult();
        Console.WriteLine($"Import: installed {HcgConnectConfiguration.FileName} in {paths.Root} (client {result.Configuration!.ClientId}, issuer {result.Configuration.Issuer}).");
        return Check(result.Configuration, check);
    }

    /// <summary>Exit 0 when sign-in can be attempted (this does not prove sign-in works), 5 otherwise.</summary>
    private static int Check(HcgConnectConfiguration config, HcgConnectionCheck check)
    {
        var result = check.RunAsync(config).GetAwaiter().GetResult();
        foreach (var step in result.Steps) Console.WriteLine($"Check {step.Name}: {step.State} — {step.Detail}");
        Console.WriteLine(result.ReadyToSignIn
            ? "Connection check: sign-in can be attempted. Only a completed sign-in proves it works."
            : "Connection check: sign-in cannot work with this configuration yet (see the failed checks above).");
        return result.ReadyToSignIn ? 0 : 5;
    }

    private static int Export(string? folder, HcgConnectConfiguration config)
    {
        if (folder is null) return Usage();
        foreach (var f in HcgRegistrationExport.Write(Path.GetFullPath(folder), config)) Console.WriteLine("Wrote " + f);
        Console.WriteLine("Nothing was sent. Give the registration request to HCG; HCG returns the template with the issued client ID.");
        return 0;
    }

    private static int Registration(HcgConnectConfiguration config)
    {
        Console.WriteLine("Client registration HCG needs for HCG Executable Maker (RFC 7591 metadata, from this build's configuration).");
        Console.WriteLine("Register it centrally (ERP > App integrations); never reuse another app's client. No secret is requested or accepted.");
        Console.WriteLine(config.RegistrationRequestJson());
        return 0;
    }

    /// <summary>Exit code for a decision: 0 allowed, 3 sign-in needed, 8 not permitted, 9 unavailable / not available yet.</summary>
    internal static int ExitCode(FeatureDecision d) => d.State switch
    {
        FeatureAccessState.Free or FeatureAccessState.Allowed => 0,
        FeatureAccessState.SignInRequired or FeatureAccessState.NotConfigured => 3,
        FeatureAccessState.NotPermitted or FeatureAccessState.ChooseOrganisation => 8,
        _ => 9,
    };

    private static void PrintDecision(FeatureDecision d) =>
        Console.WriteLine($"Feature {d.Feature.Id} ({d.Feature.Title}): {d.StateLabel} — {d.Explanation}" +
                          (d.RemedyText is { Length: > 0 } r ? " " + r : "") + (d.Link is { } l ? " " + l : "") +
                          (d.Reason is { } why ? $" [HCG {d.Layer ?? "?"}/{why}]" : ""));

    /// <summary>Restores the stored session; with --sign-in, signs in when there is none (needed where no keyring keeps it).</summary>
    private static void Restore(HcgAccountService account, bool signIn)
    {
        account.InitializeAsync().GetAwaiter().GetResult();
        if (account.IsSignedIn || !signIn || account.Status == HcgAccountStatus.NotConfigured) return;
        ISystemBrowser browser = account.Configuration.UsesLocalIdentityServer ? new LocalTestServerBrowser() : new ProcessBrowser();
        account.SignInAsync(browser).GetAwaiter().GetResult();
    }

    private static int Features(HcgAccountService account, FeatureAccessService features, bool signIn)
    {
        Restore(account, signIn);
        Print(account);
        if (account.IsSignedIn) features.RefreshAsync("features command").GetAwaiter().GetResult();
        Console.WriteLine($"Access: {features.Status} — {features.StatusMessage}");
        foreach (var d in features.DecideAll()) PrintDecision(d);
        return 0;
    }

    private static int Require(string? id, HcgAccountService account, FeatureAccessService features, bool signIn)
    {
        if (id is null) return Usage();
        MakerFeature feature;
        try { feature = MakerFeatures.ById(id); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message + " Known: " + string.Join(", ", MakerFeatures.All.Select(f => f.Id))); return 2; }
        Restore(account, signIn);
        var d = features.CheckNowAsync(feature).GetAwaiter().GetResult();
        PrintDecision(d);
        return ExitCode(d);
    }

    private static int Status(HcgAccountService account, AccountPlanService plan)
    {
        account.InitializeAsync().GetAwaiter().GetResult();
        Print(account);
        if (!account.IsSignedIn) return 3;
        plan.RefreshAsync().GetAwaiter().GetResult();
        PrintPlan(plan);
        return 0;
    }

    private static int SignIn(HcgAccountService account, AccountPlanService plan)
    {
        if (account.Status is HcgAccountStatus.NotConfigured)
        {
            Print(account);
            return 4;
        }
        ISystemBrowser browser = account.Configuration.UsesLocalIdentityServer ? new LocalTestServerBrowser() : new ProcessBrowser();
        if (!account.Configuration.UsesLocalIdentityServer)
            Console.WriteLine("Opening HCG sign-in in your browser. Finish there; this command waits up to 5 minutes.");
        account.SignInAsync(browser).GetAwaiter().GetResult();
        Print(account);
        if (account.Status == HcgAccountStatus.ClientRejected) return 7; // HCG's own answer: the client is not accepted
        if (!account.IsSignedIn) return 3;
        plan.RefreshAsync().GetAwaiter().GetResult();
        PrintPlan(plan);
        return 0;
    }

    private static int SignOut(HcgAccountService account)
    {
        account.InitializeAsync().GetAwaiter().GetResult();
        account.SignOutAsync().GetAwaiter().GetResult();
        Print(account);
        return account.IsSignedIn ? 1 : 0;
    }

    private static void Print(HcgAccountService account)
    {
        Console.WriteLine($"HCG account: {account.Status} — {account.Message}");
        if (account.Identity is { } id) Console.WriteLine($"Account ID (from HCG): {id.Subject} · {id.Email ?? "(no e-mail)"}");
        if (account.IsSignedIn && account.SessionExpiresAt is { } e) Console.WriteLine($"Session: renews automatically (current token until {e:u})");
    }

    private static void PrintPlan(AccountPlanService plan)
    {
        Console.WriteLine($"Plan and access: {plan.State} — {plan.Message}");
        Console.WriteLine($"Plan: {plan.PlanDisplay} · valid until: {(plan.ValidUntil is { } v ? v.ToString("u") : "not reported by HCG")}");
        Console.WriteLine($"Organisation: {(plan.OrganisationId is { } o ? plan.Organisations.First(x => x.Id == o).Name : plan.Organisations.Count == 0 ? "(none)" : "(not chosen)")}");
        foreach (var d in plan.Features.DecideAll()) PrintDecision(d);
        foreach (var l in plan.Access?.Licences ?? [])
            Console.WriteLine($"Licence {l.Scope}{(l.ScopeKey is { } k ? " " + k : "")}: {l.Status ?? "status not reported"}" + (l.EndsAt is { } e ? $" until {e:u}" : "") +
                              (l.AppliesTo(FeatureAccessService.EditionKey) ? "" : " (another edition; does not apply)"));
        if (plan.Access?.Links.Manage is { } m) Console.WriteLine("Manage link (from HCG): " + m);
    }

    /// <summary>The user's default browser (system browser), for real HCG sign-in from a terminal.</summary>
    internal sealed class ProcessBrowser : ISystemBrowser
    {
        public Task<bool> OpenAsync(Uri url)
        {
            try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); return Task.FromResult(true); }
            catch (Exception) { return Task.FromResult(false); }
        }
    }

    /// <summary>
    /// Only for a local test identity server (refused for anything else): follows its authorization redirect to this
    /// app's loopback callback, as a browser would after the user approved.
    /// </summary>
    internal sealed class LocalTestServerBrowser : ISystemBrowser
    {
        public Task<bool> OpenAsync(Uri url)
        {
            if (url.Scheme != Uri.UriSchemeHttp || !url.IsLoopback) return Task.FromResult(false);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
                var next = url;
                for (var hop = 0; hop < 5 && next is not null; hop++)
                {
                    using var response = await http.GetAsync(next);
                    if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect)) return;
                    next = response.Headers.Location is { } loc ? new Uri(next, loc) : null;
                    if (next is not null && (next.Scheme != Uri.UriSchemeHttp || !next.IsLoopback)) return; // never leaves this computer
                }
            });
            return Task.FromResult(true);
        }
    }
}
