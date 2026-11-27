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
/// [--make-current] [--supersede-compatible]   ask the publisher (recorded with the draft; applied only at publication)
/// [--add-to-release &lt;id&gt;]   Add to Listed Product: an open draft takes the files; otherwise a new revision is prepared
/// [--description "…"] [--category c] [--platform p]…   descriptive fields for --draft-product
/// [--registration-out &lt;folder&gt;]   with --draft-product: also write the central registration request for HCG (not uploaded)
/// --store-catalogue   the products HCG lets you manage, with official metadata and version history (read only)
/// --store-history --product &lt;id&gt;   every release of one product (read only)
/// --store-compare --product &lt;id&gt; --release-version V --build-number B [--file …]   version comparison (read only;
///                     exit 6 when HCG Store would refuse the version)
/// --store-request &lt;disable|restore|supersede|make-current&gt; --release &lt;id&gt; --reason "…" --confirm &lt;version&gt;
///                     records an availability request for the people who approve and publish (changes nothing)
/// [--sign-in]         sign in first when no session is stored
/// --manifest &lt;store-submission.json&gt;   take the release and the files from a submission manifest (private draft only)
/// --store-status      connection diagnostics: eleven separate checks with HCG Store's actual answers (redacted;
///                     changes nothing). Exit 0 when private drafts can be uploaded, 5 when not, 4 when unreachable.
/// [--diagnostics-out &lt;file&gt;] also write the diagnostics text to a file; with --sign-in, signs in first when needed
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
        if (args.Contains("--store-status")) return StoreStatus(services, StoreOrigin.Parse(Arg("--store")), Arg("--diagnostics-out"), args.Contains("--sign-in"));
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
            StoreCapabilityFlags? flags = who.Capabilities;
            try { flags = client.CapabilitiesAsync().GetAwaiter().GetResult().Capabilities ?? flags; }
            catch (HcgStoreException ex) { Console.WriteLine($"Capabilities: not read ({ex.Code}, HTTP {ex.Status})."); }
            Console.WriteLine($"Publishing mode {who.PublishingMode ?? "not stated"}; capabilities: {(flags is null ? "none stated" : StoreConnectionDiagnostics.FlagsText(flags))}");
            if (args.Contains("--store-catalogue")) return Catalogue(client, flags);
            if (args.Contains("--store-history")) return History(client, Arg("--product") ?? throw new ArgumentException("--store-history needs --product"));
            if (Arg("--store-request") is { } action)
            {
                var recorded = client.RequestAsync(Arg("--release") ?? throw new ArgumentException("--store-request needs --release"), action,
                    Arg("--reason") ?? throw new ArgumentException("--store-request needs --reason"), Arg("--confirm") ?? throw new ArgumentException("--store-request needs --confirm <version>")).GetAwaiter().GetResult();
                Console.WriteLine($"Request recorded: {recorded.Recorded}. {recorded.Note}");
                foreach (var q in recorded.Requests) Console.WriteLine($"  {q.Action}: {q.Reason} (release was {q.Status}, via {q.Via})");
                return recorded.Recorded ? 0 : 1;
            }
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
                var platforms = All("--platform").ToList();
                var p = flags?.ProductMetadata == true
                    ? client.CreateDraftProductAsync(new StoreDraftProductRequest { Id = product, Title = title, Description = Arg("--description"), Category = Arg("--category"), Platforms = platforms.Count > 0 ? platforms : null }).GetAwaiter().GetResult()
                    : client.CreateDraftProductAsync(product, title).GetAwaiter().GetResult();
                Console.WriteLine($"Draft product {p.Id} ({p.Title}): {p.Note}");
                if (Arg("--registration-out") is { } outDir)
                {
                    var path = ProductRegistrationRequest.WriteAsync(Path.GetFullPath(outDir), new ProductRegistrationRequest.Input(product, title, Arg("--description") ?? "", Arg("--category") ?? "",
                        platforms, Arg("--edition") ?? "", Arg("--release-version") ?? "", Arg("--icon") is { } ic ? Path.GetFullPath(ic) : null, Arg("--banner") is { } bn ? Path.GetFullPath(bn) : null,
                        who.Email, origin.ToString())).GetAwaiter().GetResult();
                    Console.WriteLine($"Central registration request for HCG: {path} (not uploaded; HCG decides the central ID, editions, artwork and listing).");
                }
                if (All("--file").FirstOrDefault() is null && Arg("--manifest") is null) return 0;
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
            if (args.Contains("--store-compare"))
            {
                IReadOnlyList<StoreRelease> h0 = client.ReleasesAsync(product).GetAwaiter().GetResult();
                var cmp = Compare(client, product, h0, Arg("--edition"), Arg("--channel") ?? "beta", Arg("--release-version") ?? "", Arg("--build-number") ?? "", files);
                foreach (var d in DistributionStudio.Discrepancies(Arg("--release-version") ?? "", Arg("--build-number") ?? "", null, files)) Console.WriteLine($"  [{d.Level}] {d.Code}: {d.Message}");
                return cmp.Blocked ? 6 : 0;
            }
            // Add to Listed Product: the release to extend decides the version, build, edition and channel.
            string? continueRelease = Arg("--release");
            string? version = Arg("--release-version"), build = Arg("--build-number"), edition = Arg("--edition"), channel = Arg("--channel");
            IReadOnlyList<StoreRelease> history = [];
            try { history = client.ReleasesAsync(product).GetAwaiter().GetResult(); } catch (HcgStoreException ex) { Console.WriteLine($"History: not read ({ex.Code})."); }
            if (Arg("--add-to-release") is { } addTo)
            {
                var target = history.FirstOrDefault(r => r.Id == addTo) ?? client.ReleaseAsync(addTo).GetAwaiter().GetResult();
                var plan = DistributionStudio.PlanAddition(target, history);
                Console.WriteLine("Add to listed product: " + plan.Explanation);
                if (plan.AddsToOpenDraft) continueRelease = plan.ReleaseId;
                (version, build, edition, channel) = (plan.Version, plan.Build, target.EditionId, target.Channel ?? channel);
            }
            var discrepancies = DistributionStudio.Discrepancies(version ?? "", build ?? "", null, files);
            foreach (var d in discrepancies) Console.WriteLine($"  {d.Message} (recorded in the draft's compatibility notes)");
            var request = manifest?.Release ?? new StoreReleaseRequest
            {
                ProductId = product, EditionId = edition, Version = version ?? "", Build = build ?? "",
                Channel = channel ?? "beta", ReleaseNotes = All("--note").ToList(),
                Compatibility = new StoreCompatibility
                {
                    MinimumOs = files.Where(f => f.MinimumOs is not null).GroupBy(f => f.Platform!).ToDictionary(g => g.Key, g => g.First().MinimumOs!),
                    Architectures = files.SelectMany(f => f.Architectures).Distinct().ToList(),
                    Notes = DistributionStudio.CompatibilityNotes(All("--compat-note"), discrepancies),
                },
                Distribution = Arg("--distribution") ?? "paid", Eligibility = Arg("--eligibility") ?? "private",
                Destinations = args.Contains("--hcg-repo") ? ["hcg-store", "hcg-repo"] : ["hcg-store"],
                PublicationRequest = DistributionStudio.PublicationRequest(args.Contains("--make-current"), args.Contains("--supersede-compatible"), flags),
            };
            if (manifest is null && Compare(client, product, history, request.EditionId, request.Channel, request.Version, request.Build, files) is { Blocked: true } && continueRelease is null)
                return 6;
            if ((args.Contains("--make-current") || args.Contains("--supersede-compatible")) && request.PublicationRequest is null)
                Console.WriteLine("Publication request: this HCG Store does not take publication requests; nothing was asked. Ask the publisher in HCG Store.");
            var meters = new Dictionary<string, (DateTime At, long Bytes)>();
            var result = new ShipToStoreService(client, features).ShipAsync(request, files, continueRelease,
                new Progress<ShipItemProgress>(p =>
                {
                    var speed = "";
                    if (p.State == ShipItemState.Uploading)
                    {
                        var now = DateTime.UtcNow;
                        if (meters.TryGetValue(p.FileName, out var m) && (now - m.At).TotalSeconds > 0.2 && p.Received > m.Bytes) speed = $" ({(p.Received - m.Bytes) / (now - m.At).TotalSeconds / 1048576:0.0} MB/s)";
                        if (!meters.ContainsKey(p.FileName) || speed.Length > 0) meters[p.FileName] = (now, p.Received);
                    }
                    if (p.State != ShipItemState.Uploading || p.Detail is not null || p.Received == p.Total || speed.Length > 0) Console.WriteLine($"  {p.FileName}: {p.State} {p.Received:N0}/{p.Total:N0}{speed} {p.Detail}");
                }),
                CancellationToken.None).GetAwaiter().GetResult();
            var r = result.Release;
            if (result.ContinuedExistingDraft) Console.WriteLine($"Continued the existing draft {r.Id} (same version, build and details): no second release was created.");
            Console.WriteLine($"Release {r.Id}: {r.ProductTitle ?? r.ProductId} {r.Version} build {r.Build} — {r.Status}; requested {r.RequestedEligibility}; distribution {r.Distribution}");
            if (r.PublicationRequest is { } pr) Console.WriteLine($"Publication request recorded: make current {pr.MakeCurrent}, supersede {pr.SupersedePrevious} (applied only when a person publishes in HCG Store).");
            foreach (var a in r.Artifacts) Console.WriteLine($"  {a.FileName}: {a.Status} · SHA-256 {a.Sha256 ?? a.DeclaredSha256}" +
                                                             (a.Kind is null ? "" : $" · {StoreArtifacts.KindName(a.Kind)}{(a.KindCheckedByStore ? " (checked by HCG Store)" : "")}") +
                                                             (a.Readiness is { } rd ? $"\n    HCG Store: {rd.Label}" : ""));
            foreach (var i in result.Items.Where(i => i.Passport is not null)) Console.WriteLine($"  {i.FileName}: Release Passport {i.Passport}");
            Console.WriteLine($"Review in HCG Store: {r.ReviewUrl}");
            PrintManager(client, r.Id);
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
                PrintManager(client, r.Id);
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

    /// <summary>The authorised catalogue with official metadata and each product's history, as HCG Store answers.</summary>
    private static int Catalogue(HcgStoreClient client, StoreCapabilityFlags? flags)
    {
        var products = StoreCatalogue.EnrichAsync(client, client.ProductsAsync().GetAwaiter().GetResult(), flags).GetAwaiter().GetResult();
        Console.WriteLine($"Catalogue: {products.Count} product(s) you may manage{(flags?.ProductMetadata == true ? " (official metadata from HCG Store)" : " (summaries from HCG Store's public release resolution)")}.");
        foreach (var p in products)
        {
            Console.WriteLine($"- {p.Title} [{p.Id}] {p.Source}; status {p.DevelopmentStatus ?? "not stated"}; category {p.Category ?? "not stated"}; icon {(p.Icon is null ? "not stated" : "yes")}");
            Console.WriteLine($"    platforms: {string.Join(", ", StoreCatalogue.PlatformsOf(p))}; editions: {(p.Editions.Count == 0 ? "none" : string.Join(", ", p.Editions.Select(e => e.Id)))}; current release: {p.CurrentReleaseId ?? "none stated"}");
            if (p.Summary is { } sm) Console.WriteLine($"    {sm}");
            try
            {
                var h = client.ReleasesAsync(p.Id).GetAwaiter().GetResult();
                Console.WriteLine($"    history: {h.Count} release(s){(h.Count > 0 ? ": " + string.Join(", ", h.Take(5).Select(r => $"{r.Version} ({r.Build}) {r.Stage ?? r.Status}")) : "")}");
            }
            catch (HcgStoreException ex) { Console.WriteLine($"    history: not read ({ex.Code})"); }
        }
        return products.Count > 0 ? 0 : 5;
    }

    private static int History(HcgStoreClient client, string productId)
    {
        var h = client.ReleasesAsync(productId).GetAwaiter().GetResult();
        Console.WriteLine($"History of {productId}: {h.Count} release(s)");
        foreach (var r in h)
        {
            Console.WriteLine($"- {r.Id} {r.Version} build {r.Build} · {r.Channel} · {r.Status} ({r.Stage}){(r.SupersededBy is { } s ? " · superseded by " + s : "")}{(r.WithdrawnReason is { } w ? " · withdrawn: " + w : "")}");
            foreach (var a in r.Artifacts.Where(a => a.Status != "removed")) Console.WriteLine($"    {a.FileName}: {a.Platform} {string.Join("/", a.Architectures)} {a.Format} · {a.Status} · SHA-256 {a.Sha256 ?? a.DeclaredSha256}");
            if (r.PublicationRequest is { } pr) Console.WriteLine($"    publication request: make current {pr.MakeCurrent}, supersede {pr.SupersedePrevious}");
            foreach (var q in r.Requests) Console.WriteLine($"    request: {q.Action} — {q.Reason}");
        }
        return 0;
    }

    /// <summary>Prints the version comparison (read only) and returns it.</summary>
    private static VersionComparison Compare(HcgStoreClient client, string productId, IReadOnlyList<StoreRelease> history, string? edition, string channel, string version, string build, IReadOnlyList<StoreArtifactCandidate> files)
    {
        StoreProduct? product = null;
        try { product = client.ProductsAsync().GetAwaiter().GetResult().FirstOrDefault(p => p.Id == productId); } catch (HcgStoreException) { }
        var c = DistributionStudio.Compare(product, history, edition, channel, version, build, files);
        Console.WriteLine($"Version comparison: current published {(c.CurrentPublished is { } cp ? $"{cp.Version} ({cp.Build}, {c.CurrentBasis})" : "none")}; latest private draft {(c.LatestPrivateDraft is { } d ? $"{d.Version} ({d.Build}, {d.Stage ?? d.Status})" : "none")}; proposed {c.Proposed}");
        foreach (var pr in c.PlatformReleases) Console.WriteLine($"  published: {pr.Platform} {pr.Architecture} {pr.Format} → {pr.Version} ({pr.Build}, {pr.Channel})");
        foreach (var f in c.Findings) Console.WriteLine($"  [{f.Level}] {f.Code}: {f.Message}");
        return c;
    }

    /// <summary>What HCG Store's Release Manager lists for this release, read back from the Store.</summary>
    private static void PrintManager(HcgStoreClient client, string releaseId)
    {
        try
        {
            var m = client.ReleaseManagerAsync().GetAwaiter().GetResult();
            Console.WriteLine(m.Releases.FirstOrDefault(x => x.Id == releaseId) is { } e
                ? $"Release Manager: {e.ProductTitle ?? e.ProductId} {e.Version} build {e.Build} — stage {e.Stage ?? e.Status}, {e.Files} file(s); audit trail {(m.AuditIntact == true ? "intact" : "NOT intact")}"
                : $"Release Manager: release {releaseId} is not listed.");
        }
        catch (HcgStoreException ex) { Console.WriteLine($"Release Manager: not read ({ex.Code}, HTTP {ex.Status}: {ex.Message})"); }
    }

    /// <summary>
    /// Ship to Store connection diagnostics without the UI: the same eleven checks as the page, each with HCG Store's actual
    /// answer and what to do. Read only; nothing is created, uploaded, submitted or published. Redacted.
    /// </summary>
    private static int StoreStatus(IServiceProvider services, Uri? origin, string? writeTo, bool signIn)
    {
        var factory = services.GetRequiredService<StoreClientFactory>();
        origin ??= factory.OriginAsync().GetAwaiter().GetResult();
        var account = services.GetRequiredService<HcgAccountService>();
        account.InitializeAsync().GetAwaiter().GetResult();
        if (!account.IsSignedIn && signIn)
        {
            ISystemBrowser browser = account.Configuration.UsesLocalIdentityServer ? new HcgSessionCommand.LocalTestServerBrowser() : new HcgSessionCommand.ProcessBrowser();
            account.SignInAsync(browser).GetAwaiter().GetResult();
        }
        var ctx = new StoreDiagnosticsContext(account.IsSignedIn, account.Identity?.Email, account.Message, account.Configuration, HostInfo.ProductVersion);
        var report = StoreConnectionDiagnostics.RunAsync(factory.Create(origin), ctx, account.GetAccessTokenAsync).GetAwaiter().GetResult();
        var text = report.ToText();
        Console.Write(text);
        if (writeTo is not null) File.WriteAllText(writeTo, text);
        return report["connectivity"].State == StoreCheckState.Failed ? 4 : report.CanUpload ? 0 : 5;
    }
}
