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
        return verb switch
        {
            "status" => Status(account, plan),
            "sign-in" => SignIn(account, plan),
            "sign-out" => SignOut(account),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: HCGExecutableMaker --hcg-session status|sign-in|sign-out");
        return 2;
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
        if (account.Status == HcgAccountStatus.NotConfigured)
        {
            Print(account);
            return 4;
        }
        ISystemBrowser browser = account.Configuration.UsesLocalIdentityServer ? new LocalTestServerBrowser() : new ProcessBrowser();
        if (!account.Configuration.UsesLocalIdentityServer)
            Console.WriteLine("Opening HCG sign-in in your browser. Finish there; this command waits up to 5 minutes.");
        account.SignInAsync(browser).GetAwaiter().GetResult();
        Print(account);
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
        foreach (var f in plan.Access?.Features ?? []) Console.WriteLine($"Feature {f.Key}: {(f.Allowed ? "allowed" : "not allowed")}");
        if (plan.Access?.Links.Manage is { } m) Console.WriteLine("Manage link (from HCG): " + m);
    }

    /// <summary>The user's default browser (system browser), for real HCG sign-in from a terminal.</summary>
    private sealed class ProcessBrowser : ISystemBrowser
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
    private sealed class LocalTestServerBrowser : ISystemBrowser
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
