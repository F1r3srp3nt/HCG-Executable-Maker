using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.Core;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --ship-to-store</c>: Ship to Store without the UI, with the app's own HCG Connect session and the
/// same detection, checks and upload code as the page.
/// <code>
/// --store &lt;url&gt; --product &lt;id&gt; [--edition &lt;id&gt;] [--draft-product "Title"] --release-version 1.2.0 --build-number 42
/// --file &lt;path&gt; [--file …] [--note "…"]… [--distribution free|paid|subscription|entitlement]
/// [--eligibility private|internal-testing|public] [--channel beta|stable] [--hcg-repo]
/// [--release &lt;id&gt;]   continue an existing draft (retry/resume)
/// [--verify-download] download each verified file back and compare its SHA-256
/// [--submit]          submit the draft for MANUAL review in HCG Store (never approves)
/// [--withdraw "reason"] withdraw (roll back) --release
/// [--sign-in]         sign in first when no session is stored
/// </code>
/// Exit code 0 when every requested step succeeded.
/// </summary>
internal static class ShipToStoreCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var account = services.GetRequiredService<HcgAccountService>();
        Console.WriteLine(HostInfo.BuildIdentity);
        account.InitializeAsync().GetAwaiter().GetResult();
        if (!account.IsSignedIn && args.Contains("--sign-in"))
        {
            ISystemBrowser browser = account.Configuration.UsesLocalIdentityServer ? new HcgSessionCommand.LocalTestServerBrowser() : new HcgSessionCommand.ProcessBrowser();
            account.SignInAsync(browser).GetAwaiter().GetResult();
        }
        Console.WriteLine($"HCG account: {account.Status} — {account.Message}");
        if (!account.IsSignedIn) return 3;

        var origin = StoreOrigin.Parse(Arg("--store")) ?? services.GetRequiredService<StoreClientFactory>().OriginAsync().GetAwaiter().GetResult();
        var client = services.GetRequiredService<StoreClientFactory>().Create(origin);
        var runner = services.GetRequiredService<IProcessRunner>();
        try
        {
            var who = client.WhoAmIAsync().GetAwaiter().GetResult();
            Console.WriteLine($"HCG Store {origin}: signed in as {who.Email ?? who.Sub} ({who.Via}); publisher permission {who.PublisherPermission}.");
            if (Arg("--withdraw") is { } reason)
            {
                var w = client.WithdrawAsync(Arg("--release") ?? throw new ArgumentException("--withdraw needs --release"), reason).GetAwaiter().GetResult();
                Console.WriteLine($"Release {w.Id}: {w.Status} ({w.WithdrawnReason})");
                return w.Status == "withdrawn" ? 0 : 1;
            }
            var product = Arg("--product") ?? throw new ArgumentException("--product is required");
            if (Arg("--draft-product") is { } title)
            {
                var p = client.CreateDraftProductAsync(product, title).GetAwaiter().GetResult();
                Console.WriteLine($"Draft product {p.Id} ({p.Title}): {p.Note}");
            }
            var files = new List<StoreArtifactCandidate>();
            foreach (var f in All("--file"))
            {
                var c = StoreArtifacts.DetectAsync(Path.GetFullPath(f)).GetAwaiter().GetResult();
                c = c with { Signing = StoreArtifacts.CheckSigningAsync(c, runner).GetAwaiter().GetResult() };
                Console.WriteLine($"File {c.FileName}: {c.Label} · {c.KindName}, {c.SizeBytes:N0} bytes, SHA-256 {c.Sha256}, signature {ShipToStoreService.DeclaredSigning(c).Status}" +
                                  (c.MinimumOs is null ? "" : $", minimum OS {c.MinimumOs}") + (c.Package is { } pk ? $", package {pk.Id} {pk.Version} {pk.Architecture}" : "") +
                                  $"\n  install: {c.Readiness.Label}" +
                                  string.Concat(c.Problems.Select(x => "\n  PROBLEM " + x)) + string.Concat(c.Notes.Select(x => "\n  note " + x)));
                files.Add(c);
            }
            var request = new StoreReleaseRequest
            {
                ProductId = product, EditionId = Arg("--edition"), Version = Arg("--release-version") ?? "", Build = Arg("--build-number") ?? "",
                Channel = Arg("--channel") ?? "beta", ReleaseNotes = All("--note").ToList(),
                Compatibility = new StoreCompatibility
                {
                    MinimumOs = files.Where(f => f.MinimumOs is not null).GroupBy(f => f.Platform!).ToDictionary(g => g.Key, g => g.First().MinimumOs!),
                    Architectures = files.SelectMany(f => f.Architectures).Distinct().ToList(),
                },
                Distribution = Arg("--distribution") ?? "paid", Eligibility = Arg("--eligibility") ?? "private",
                Destinations = args.Contains("--hcg-repo") ? ["hcg-store", "hcg-repo"] : ["hcg-store"],
            };
            var result = new ShipToStoreService(client).ShipAsync(request, files, Arg("--release"),
                new Progress<ShipItemProgress>(p => { if (p.State != ShipItemState.Uploading || p.Detail is not null || p.Received == p.Total) Console.WriteLine($"  {p.FileName}: {p.State} {p.Received:N0}/{p.Total:N0} {p.Detail}"); }),
                CancellationToken.None).GetAwaiter().GetResult();
            var r = result.Release;
            if (result.ContinuedExistingDraft) Console.WriteLine($"Continued the existing draft {r.Id} (same version, build and details): no second release was created.");
            Console.WriteLine($"Release {r.Id}: {r.ProductTitle ?? r.ProductId} {r.Version} build {r.Build} — {r.Status}; requested {r.RequestedEligibility}; distribution {r.Distribution}");
            foreach (var a in r.Artifacts) Console.WriteLine($"  {a.FileName}: {a.Status} · SHA-256 {a.Sha256 ?? a.DeclaredSha256}" +
                                                             (a.Kind is null ? "" : $" · {StoreArtifacts.KindName(a.Kind)}{(a.KindCheckedByStore ? " (checked by HCG Store)" : "")}") +
                                                             (a.Readiness is { } rd ? $"\n    HCG Store: {rd.Label}" : ""));
            Console.WriteLine($"Review in HCG Store: {r.ReviewUrl}");
            var ok = result.AllVerified;
            if (args.Contains("--verify-download"))
                foreach (var a in r.Artifacts.Where(a => a.Status == "verified"))
                {
                    var (matches, sha, bytes) = client.DownloadAndVerifyAsync(a.Id, a.Sha256!, null, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"Download {a.FileName}: {bytes:N0} bytes, SHA-256 {sha} — {(matches ? "matches" : "DOES NOT MATCH")}");
                    ok &= matches;
                }
            if (args.Contains("--submit") && ok)
            {
                var s = client.SubmitAsync(r.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Submitted for manual review: {s.Status}. An approver approves it in HCG Store; nothing is published automatically.");
            }
            return ok ? 0 : 1;
        }
        catch (HcgStoreException ex)
        {
            Console.Error.WriteLine($"HCG Store: {ex.Message} ({ex.Code}, HTTP {ex.Status})");
            return 4;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
