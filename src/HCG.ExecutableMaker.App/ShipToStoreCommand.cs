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
/// [--passport &lt;release-passport.json&gt;] build provenance for the files (otherwise a passport next to each file is used)
/// [--release &lt;id&gt;]   continue an existing draft (retry/resume)
/// [--verify-download] download each verified file back and compare its SHA-256
/// [--submit]          submit the draft for MANUAL review in HCG Store (never approves)
/// [--withdraw "reason"] withdraw (roll back) --release
/// [--sign-in]         sign in first when no session is stored
/// --manifest &lt;store-submission.json&gt;   take the release and the files from a submission manifest (private draft only)
/// --store-status      read the Store's public health and the publishing API's answer; changes nothing
/// </code>
/// Exit code 0 when every requested step succeeded.
/// </summary>
internal static class ShipToStoreCommand
{
    /// <summary>What Maker reports to HCG Store about how the file was built (from a release passport of these exact bytes).</summary>
    internal static string ProvenanceText(StoreProvenance.Result? p) => p is null ? "not examined"
        : (p.SourceCommit is null && p.BuildProvider is null && p.Toolchain is null ? "none reported" : $"commit {p.SourceCommit ?? "—"}, provider {p.BuildProvider ?? "—"}, toolchain {p.Toolchain ?? "—"}")
          + (p.PassportPath is { } pp ? $" (from {Path.GetFileName(pp)})" : "") + string.Concat(p.Notes.Select(n => "; " + n));

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var account = services.GetRequiredService<HcgAccountService>();
        Console.WriteLine(HostInfo.BuildIdentity);
        if (args.Contains("--store-status")) return StoreStatus(services, StoreOrigin.Parse(Arg("--store")));
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
        // Enforced at command execution by the same shared service the UI uses: HCG Store decides the staff permission.
        var features = services.GetRequiredService<FeatureAccessService>();
        var decision = features.CheckNowAsync(MakerFeatures.StorePublish, storeOrigin: origin).GetAwaiter().GetResult();
        Console.WriteLine($"Feature {decision.Feature.Id}: {decision.StateLabel} — {decision.Explanation}" + (decision.RemedyText is { Length: > 0 } rt ? " " + rt : ""));
        if (!decision.IsAllowed) return HcgSessionCommand.ExitCode(decision);
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
            (StoreReleaseRequest Release, IReadOnlyList<(string Path, string Sha256, long Size)> Files)? manifest =
                Arg("--manifest") is { } mf ? StoreSubmission.Load(Path.GetFullPath(mf)) : null;
            var product = manifest?.Release.ProductId ?? Arg("--product") ?? throw new ArgumentException("--product is required");
            if (Arg("--draft-product") is { } title)
            {
                var p = client.CreateDraftProductAsync(product, title).GetAwaiter().GetResult();
                Console.WriteLine($"Draft product {p.Id} ({p.Title}): {p.Note}");
            }
            var files = new List<StoreArtifactCandidate>();
            foreach (var f in manifest is { } m0 ? m0.Files.Select(x => x.Path) : All("--file"))
            {
                var c = StoreArtifacts.DetectAsync(Path.GetFullPath(f)).GetAwaiter().GetResult();
                if (manifest is { } m1 && m1.Files.First(x => x.Path == f) is var declared && (declared.Sha256 != c.Sha256 || declared.Size != c.SizeBytes))
                    throw new InvalidOperationException($"{c.FileName} changed since the submission manifest was made (SHA-256 {c.Sha256}, manifest {declared.Sha256}): nothing was sent.");
                c = c with { Signing = StoreArtifacts.CheckSigningAsync(c, runner).GetAwaiter().GetResult() };
                if (Arg("--passport") is { } passport && c.Sha256.Length == 64) c = c with { Provenance = StoreProvenance.ForFile(c.Path, c.Sha256, c.SizeBytes, Path.GetFullPath(passport)) };
                Console.WriteLine($"File {c.FileName}: {c.Label} · {c.KindName}, {c.SizeBytes:N0} bytes, SHA-256 {c.Sha256}, signature {ShipToStoreService.DeclaredSigning(c).Status}" +
                                  (c.MinimumOs is null ? "" : $", minimum OS {c.MinimumOs}") + (c.Package is { } pk ? $", package {pk.Id} {pk.Version} {pk.Architecture}" : "") +
                                  $"\n  install: {c.Readiness.Label}" +
                                  $"\n  provenance: {ProvenanceText(c.Provenance)}" +
                                  string.Concat(c.Problems.Select(x => "\n  PROBLEM " + x)) + string.Concat(c.Notes.Select(x => "\n  note " + x)));
                files.Add(c);
            }
            var request = manifest?.Release ?? new StoreReleaseRequest
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
            var result = new ShipToStoreService(client, features).ShipAsync(request, files, Arg("--release"),
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
                features.RequireAsync(MakerFeatures.StorePublish, storeOrigin: origin).GetAwaiter().GetResult();
                var s = client.SubmitAsync(r.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Submitted for manual review: {s.Status}. An approver approves it in HCG Store; nothing is published automatically.");
            }
            return ok ? 0 : 1;
        }
        catch (FeatureAccessDeniedException ex)
        {
            Console.Error.WriteLine($"Feature {ex.Decision.Feature.Id}: {ex.Decision.StateLabel} — {ex.Decision.Explanation} {ex.Decision.RemedyText}".TrimEnd());
            return HcgSessionCommand.ExitCode(ex.Decision);
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

    /// <summary>
    /// What the HCG Store server at this address states about itself (public health, no sign-in), and how its
    /// publishing API answers this account. Read only. The deployed server can be older than the Store's main branch.
    /// </summary>
    private static int StoreStatus(IServiceProvider services, Uri? origin)
    {
        var factory = services.GetRequiredService<StoreClientFactory>();
        origin ??= factory.OriginAsync().GetAwaiter().GetResult();
        var client = factory.Create(origin);
        try
        {
            var h = client.HealthAsync().GetAwaiter().GetResult();
            Console.WriteLine(h is null ? $"{origin}: not an HCG Store server (no JSON health answer)."
                : $"{origin}: status {h.Status}, mode {h.Mode}, HCG Connect {h.HcgConnect}, entitlements {h.EntitlementAuthority}, payments {h.Payments}, protected downloads {h.ProtectedDownloads}" +
                  (h.Publishing is { } pub ? $", publishing {pub}" : " (publishing is not stated by this server's health answer)"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Console.WriteLine($"{origin}: unreachable — {ex.Message}"); return 4; }
        if (!services.GetRequiredService<HcgAccountService>().IsSignedIn) { Console.WriteLine("Publishing API: not checked (not signed in to HCG)."); return 0; }
        try
        {
            var who = client.WhoAmIAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Publishing API: enabled; signed in as {who.Email ?? who.Sub}; publisher permission {who.PublisherPermission}; approver permission {who.ApproverPermission}.");
        }
        catch (HcgStoreException ex)
        {
            Console.WriteLine($"Publishing API: {StoreDiagnosis.Classify(ex, origin).Explanation} ({ex.Code}, HTTP {ex.Status}: {ex.Message})");
        }
        return 0;
    }
}
