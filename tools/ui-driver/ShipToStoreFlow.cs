using System.Text.Json;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;
using AppClass = HCG.ExecutableMaker.App.App;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>Ship to Store page, headless: the files from --source are added as from the Artifacts page; detection,
/// signature check and the signed-out state are shown (no Store is contacted without a session).
/// --ship-build: a REAL build in New Build, then "Ship to Store…" from its result, HCG Connect sign-in and the upload as
/// a private draft, against the TEST HCG Store server (--ship-store &lt;server.json&gt; from server/test/ship-e2e-server.ts).</summary>
internal static partial class Program
{
    private static async Task<int> ShipPageAsync(MainWindowViewModel main)
    {
        var files = _args["source"].Split(',').Select(Path.GetFullPath).ToArray();
        await main.NavigateAsync("ship", files);
        var page = (ShipToStoreViewModel)main.CurrentPage!;
        await WaitUntil(() => page.Files.Count == files.Length && page.Files.All(f => !f.State.StartsWith("Checking", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "file detection");
        foreach (var f in page.Files) Log($"File {f.FileName}: {f.Platform} · {f.Kind} · [{f.Install.Text}] {f.InstallDetail} · {f.Size} · {f.Signing} · {f.State}{(f.HasProblems ? " · " + f.Problems : "")}");
        Log($"Connection: [{page.Connection}] {page.ConnectionDetail}");
        Check(page.Files.All(f => f.Candidate.IsSupported), "Every sample file is detected as a supported platform artifact");
        Check(page.Connection == "Sign in required", "Without an HCG Connect session the page asks for sign-in and contacts no Store");
        Shot("ship-to-store");
        if (_args.ContainsKey("ship-store"))
        {
            // With a TEST HCG Store: sign in, then Check connection shows the ten diagnostics on the page, the catalogue,
            // the version history and comparison, a private upload and the Synchronization Center.
            using var info = JsonDocument.Parse(File.ReadAllText(_args["ship-store"]));
            await main.NavigateAsync("account", null);
            var account = (AccountViewModel)main.CurrentPage!;
            await Task.Delay(300);
            account.SignInCommand.Execute(null);
            await WaitUntil(() => account.IsSignedIn, TimeSpan.FromSeconds(60), "HCG Connect sign-in");
            await main.NavigateAsync("ship", files);
            page = (ShipToStoreViewModel)main.CurrentPage!;
            Check(page.Title == "Distribution Studio", "The Ship to Store navigation entry opens Distribution Studio");
            page.StoreUrl = info.RootElement.GetProperty("storeOrigin").GetString()!;
            await page.ConnectCommand.ExecuteAsync(null);
            Log($"Connection: [{page.Connection}] {page.ConnectionDetail}");
            Log(page.CapabilitiesText);
            foreach (var c in page.Diagnostics) Log($"  [{c.Check.StateLabel}] {c.Check.Title}: {c.Check.Summary}");
            Check(page.Diagnostics.Count == 10, "Ten separate connection checks are shown");
            var expectReady = !_args.TryGetValue("ship-expect", out var expect) || expect != "off";
            Check(expectReady ? page.Report?.CanUpload == true && page.Products.Count > 0 : page.Report?.CanUpload == false,
                expectReady ? "Ready to upload private drafts; the products HCG allows are listed" : "Not ready: the checks name what is switched off");
            Check(page.Files.All(f => f.State == "Ready to upload" && f.Progress == 0), "Opening the page and checking the connection uploaded nothing");
            await page.CopyDiagnosticsCommand.ExecuteAsync(null);
            page.DiagnosticsExpanded = true;
            ShotAt("1 · CONNECTION", "ship-diagnostics");
            page.DiagnosticsExpanded = false;
            if (!expectReady) return Failures.Count == 0 ? 0 : 1;

            // Catalogue: official metadata, search and filters, manual refresh.
            await page.RefreshProductsCommand.ExecuteAsync(null);
            foreach (var c in page.Catalogue) Log($"Catalogue: {c.Title} [{c.Identifier}] {c.Status.Text} · {c.Category} · {c.Platforms} · {c.Editions} · {c.Current} · icon {(c.HasIcon ? "loaded" : "not available")}");
            var productId = Arg("ship-product", "hcg-mail");
            page.Search = productId == "hcg-mail" ? "mail" : productId;
            Check(page.Catalogue.Any(c => c.Product.Id == productId), $"Search finds {productId}");
            page.SelectedItem = page.Catalogue.First(c => c.Product.Id == productId);
            page.SelectedEdition = page.Editions.FirstOrDefault(e => e.Id == Arg("ship-edition", "windows-desktop")) ?? page.SelectedEdition;
            page.Search = "";
            await page.LoadHistoryCommand.ExecuteAsync(null);
            Log("History: " + page.HistoryNote);
            foreach (var h in page.History) Log($"  {h.Title} [{h.Status.Text}] {h.Detail}");
            ShotAt("2 · PRODUCTS YOU MAY MANAGE", "studio-catalogue");

            // New update: version comparison and discrepancies; a private upload; the Synchronization Center.
            page.IsNewUpdate = true;
            page.Version = Arg("ship-version", "3.0.0");
            page.Build = Arg("ship-build-number", "30");
            page.ReleaseNotes = "Distribution Studio UI acceptance upload (TEST Store).";
            page.Channel = page.Channels.First(c => c.Value == "stable");
            page.Eligibility = page.Eligibilities.First(e => e.Value == "private");
            page.Distribution = page.Distributions.First(d => d.Value == "free");
            page.MakeCurrent = page.CanRequestPublication;
            page.SupersedeCompatible = page.CanRequestPublication;
            Log("Comparison: " + page.ComparisonSummary);
            foreach (var f in page.Findings) Log($"  [{f.Chip.Text}] {f.Message}");
            foreach (var d in page.Discrepancies) Log($"  [{d.Chip.Text}] {d.Message}");
            Check(page.Comparison is { Blocked: false }, "The proposed version is not a duplicate");
            ShotAt("4 · RELEASE HISTORY AND VERSIONS", "studio-compare");
            ShotAt("5 · RELEASE CONFIGURATION", "studio-release-configuration");
            ShotAt("6 · ARTIFACTS (SEVERAL FILES PER DRAFT; NO REBUILD)", "studio-artifacts");
            await page.ShipCommand.ExecuteAsync(null);
            foreach (var f in page.Files) Log($"Upload {f.FileName}: {f.State} · {f.Detail}");
            Log($"Release: {page.ReleaseStatus} · {page.ReviewUrl}");
            Log(page.ReleaseManagerStage ?? "");
            Check(page.ReleaseId is not null && page.Files.All(f => f.State is "Verified by HCG Store" or "Already uploaded"), "Uploaded to private staging and verified by HCG Store (SHA-256)");
            Check(page.ReleaseManagerStage?.Contains(page.Version, StringComparison.Ordinal) == true, "Release Manager lists the same release");
            Check(!page.CanRequestPublication || page.ReleaseStatus.Contains("request recorded", StringComparison.Ordinal), "The make-current / supersede request is recorded with the draft (applied only at publication)");
            ShotAt("7 · UPLOAD TO PRIVATE STAGING", "studio-uploaded");
            await page.RefreshSyncCommand.ExecuteAsync(null);
            Log("Sync: " + page.SyncNote);
            foreach (var g in page.Sync) Log($"  {g.Title}: {g.Group.Count}");
            Check(page.Sync.First(g => g.Group.Key == "drafts").Group.Count >= 1 && page.Sync.First(g => g.Group.Key == "uploaded").Group.Count == page.Files.Count, "Synchronization Center: from HCG Store's answers (drafts listed, uploaded files verified)");
            ShotAt("8 · SYNCHRONIZATION CENTER", "studio-sync");

            // Manage versions: the history with availability requests (typed confirmation).
            page.IsManageVersions = true;
            await page.LoadHistoryCommand.ExecuteAsync(null);
            page.SelectedRelease = page.History.FirstOrDefault(h => h.HasActions) ?? page.History.FirstOrDefault();
            if (page.SelectedRelease is { HasActions: true } target)
            {
                page.AvailabilityAction = page.AvailabilityActions.First();
                page.RequestReason = "UI acceptance: request recorded for the publisher (TEST).";
                page.RequestConfirm = "wrong";
                await page.SendAvailabilityRequestCommand.ExecuteAsync(null);
                Check(page.RequestResult?.StartsWith("Type the version", StringComparison.Ordinal) == true, "An availability request needs the version typed to confirm");
                page.RequestConfirm = target.Release.Version;
                await page.SendAvailabilityRequestCommand.ExecuteAsync(null);
                Log("Request: " + page.RequestResult);
                Check(page.RequestResult?.Contains("Nothing changed", StringComparison.Ordinal) == true, "The request is recorded in HCG Store and changes nothing");
            }
            ShotAt("4 · RELEASE HISTORY AND VERSIONS", "studio-versions");

            // New product: the draft form (not submitted here).
            page.IsNewProduct = true;
            page.NewProductTitle = "HCG Notes (TEST)";
            page.NewProductId = "hcg-notes-test";
            page.NewProductDescription = "A private product draft from the UI acceptance run.";
            page.NewProductCategory = "productivity";
            page.NewProductPlatforms.First(p => p.Value == "macos").IsSelected = true;
            ShotAt("3 · OPERATION", "studio-new-product");
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Writes Maker's HCG Connect configuration for the TEST server's mock HCG Connect (its desktop client ID).</summary>
    private static void PrepareShipStore(string dataRoot)
    {
        using var info = JsonDocument.Parse(File.ReadAllText(_args["ship-store"]));
        var r = info.RootElement;
        var issuer = r.GetProperty("issuer").GetString()!;
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, HcgConnectConfiguration.FileName), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contractVersion"] = HcgConnectConfiguration.ContractVersion, ["clientId"] = r.GetProperty("makerClientId").GetString()!, ["issuer"] = issuer,
            ["authorizationEndpoint"] = issuer + "/oauth/authorize", ["tokenEndpoint"] = issuer + "/oauth/token",
            ["userinfoEndpoint"] = issuer + "/oauth/userinfo", ["jwksEndpoint"] = issuer + "/.well-known/jwks.json",
            ["apiBaseUrl"] = issuer.Replace("/auth/v1", "", StringComparison.Ordinal),
        }));
    }

    private static async Task<int> RunShipBuildAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        using var info = JsonDocument.Parse(File.ReadAllText(_args["ship-store"]));
        var store = info.RootElement.GetProperty("storeOrigin").GetString()!;

        // 1. A real build in New Build: the .NET app cross-built for Windows x64 as a portable folder and ZIP.
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Framework} · {a.ApplicationType} · {a.Support} — {a.SupportSummary}");
        if (wizard.SelectedProject is null && wizard.ProjectOptions.Count > 0) wizard.SelectedProject = wizard.ProjectOptions[0];
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsConfigureStep && wizard.IsDotNetBuild, "Configure step for a .NET build");
        var windows = wizard.PlatformOptions.FirstOrDefault(p => p.Value == TargetPlatform.Windows && p.IsEnabled);
        Check(windows is not null, "Windows can be targeted from this computer (.NET cross-build)");
        if (windows is null) return 1;
        wizard.SelectedPlatform = windows;
        if (wizard.ArchitectureOptions.FirstOrDefault(o => o.Value == TargetArchitecture.X64) is { } x64) wizard.SelectedArchitecture = x64;
        wizard.SelfContained = true;
        wizard.OutputDirectory = Path.Combine(output, "artifacts");
        Log($"Target: {wizard.SelectedPlatform?.Name} {wizard.SelectedArchitecture?.Name} · {wizard.AppName} {wizard.Version} ({wizard.AppId})");
        foreach (var err in wizard.ConfigErrors) Log("Config error: " + err);
        if (wizard.ConfigErrors.Count > 0) return 1;
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsPackageStep, "Package step");
        foreach (var o in wizard.PackagingOptions) o.IsSelected = o.IsEnabled && o.Option.Format is PackagingFormat.PortableFolder or PackagingFormat.PortableZip;
        foreach (var o in wizard.PackagingOptions) Log($"Format {o.Name}: {o.Status.Text}{(o.IsSelected ? " (selected)" : "")}");
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsBuildStep, "Build step");
        wizard.TrustAcknowledged = true;
        await wizard.StartBuildCommand.ExecuteAsync(null);
        var result = wizard.Result;
        Check(result?.Status == BuildStatus.Succeeded, $"Real build {result?.Status}: {wizard.ResultDetail}");
        if (result?.Status != BuildStatus.Succeeded) { Shot("ship-build-failed"); return 1; }
        foreach (var art in result.Artifacts) Log($"Artifact {art.FileName}: {art.Kind} · {art.Format.DisplayName()} · {art.Verification} · SHA-256 {art.Sha256}");

        // 2. "Ship to Store…" is offered on the result, for the verified files only.
        Check(wizard.CanShipToStore, "The build result offers Ship to Store");
        Shot("ship-build-result");
        await main.NavigateAsync("account", null);
        var account = (AccountViewModel)main.CurrentPage!;
        await Task.Delay(300);
        account.SignInCommand.Execute(null);
        await WaitUntil(() => account.IsSignedIn, TimeSpan.FromSeconds(60), "HCG Connect sign-in");
        Log($"Account: [{account.AccountTag.Text}] {account.AccountMessage}");
        Check(account.IsSignedIn, "Signed in with HCG Connect (PKCE, desktop client) — the same session Ship to Store uses");
        await main.NavigateAsync("new-build", null);
        wizard = (BuildWizardViewModel)main.CurrentPage!;
        Check(wizard.CanShipToStore, "Back on the build result after signing in");
        wizard.ShipToStoreCommand.Execute(null);
        await WaitUntil(() => main.CurrentPage is ShipToStoreViewModel p && p.Files.Count > 0 && p.Files.All(f => !f.State.StartsWith("Checking", StringComparison.Ordinal)), TimeSpan.FromSeconds(60), "Ship to Store page with the build's files");
        var page = (ShipToStoreViewModel)main.CurrentPage!;
        Log("Ship page: " + page.BuildOrigin);
        foreach (var n in page.NotOffered) Log("Not offered: " + n);
        foreach (var f in page.Files) Log($"File {f.FileName}: {f.Platform} · {f.Kind} · [{f.Install.Text}] {f.InstallDetail} · {f.Signing}{(f.HasProblems ? " · " + f.Problems : "")}");
        var zip = result.Artifacts.Single(x => x.Format == PackagingFormat.PortableZip);
        Check(page.Files.Count == 1 && page.Files[0].Candidate.Path == zip.Path && page.Files[0].Sha256 == zip.Sha256, "Exactly the build's verified ZIP is offered (the SHA-256 HCG recorded at build time)");
        Check(page.NotOffered.Any(n => n.Contains("folder", StringComparison.Ordinal)), "The portable folder is listed as not offered, with the reason");
        Check(page.Files[0].Kind == "Portable app" && !page.Files[0].Candidate.Readiness.OrdinaryInstall && page.Files[0].Candidate.Readiness.Status == "unsigned",
            "The unsigned Windows build is labelled unsigned, not ready for ordinary installation");
        Check(page.Version == wizard.Version, $"Version prefilled from the build ({page.Version})");

        // 3. Product, edition, release fields; upload as a private draft.
        page.StoreUrl = store;
        await page.ConnectCommand.ExecuteAsync(null);
        Log($"Connection: [{page.Connection}] {page.ConnectionDetail}");
        foreach (var c in page.Diagnostics) Log($"  [{c.Check.StateLabel}] {c.Check.Title}: {c.Check.Summary}");
        Check(page.Report?.CanUpload == true && page.Products.Count > 0, "Connection diagnostics: HCG Store accepted the HCG Connect session (Maker's own client), HCG's permission, and listed the products HCG allows this account to publish");
        page.SelectedProduct = page.Products.FirstOrDefault(p => p.Id == Arg("ship-product", "fixture-quasar-shell"));
        page.SelectedEdition = page.Editions.FirstOrDefault(e => e.Id == Arg("ship-edition", "standard"));
        page.Build = Arg("ship-build-number", "1");
        page.ReleaseNotes = "Real HCG Executable Maker build shipped from the New Build result.";
        page.Eligibility = page.Eligibilities.First(e => e.Value == "private");
        Shot("ship-build-ready");
        await page.ShipCommand.ExecuteAsync(null);
        foreach (var f in page.Files) Log($"Upload {f.FileName}: {f.State} · {f.Detail}");
        Log($"Release: {page.ReleaseStatus} · {page.ReviewUrl}");
        Log("Summary: " + page.Summary);
        Check(page.ReleaseId is not null && page.ReleaseStatus.Contains("· draft", StringComparison.Ordinal), "A DRAFT release was created in HCG Store");
        Check(page.Files.All(f => f.State == "Verified by HCG Store"), "The build's file was uploaded and verified by HCG Store (SHA-256)");
        Check(page.ReviewUrl?.StartsWith(store, StringComparison.Ordinal) == true, "A link to review the draft in HCG Store is shown");
        await Task.Delay(TimeSpan.FromSeconds(Arg("settle-seconds") is { } st ? double.Parse(st, System.Globalization.CultureInfo.InvariantCulture) : 5));
        Check(page.Report?.CanUpload == true && page.Report.Origin.ToString().StartsWith(store, StringComparison.Ordinal), $"The page still shows the Store it shipped to (a late answer from an earlier Connect does not overwrite it): [{page.Connection}] {page.ConnectionDetail}");
        File.WriteAllText(Path.Combine(output, "ship-build.json"), JsonSerializer.Serialize(new { releaseId = page.ReleaseId, reviewUrl = page.ReviewUrl, file = zip.Path, sha256 = zip.Sha256 }));
        Shot("ship-build-uploaded");

        // 4. Shipping again (a retry) continues the same draft: no second release.
        await page.ShipCommand.ExecuteAsync(null);
        Check(page.Summary?.StartsWith("Continued the existing draft", StringComparison.Ordinal) == true || page.Files.All(f => f.State == "Already uploaded"),
            "Shipping again continues the same draft and skips the verified file: " + page.Summary);
        return Failures.Count == 0 ? 0 : 1;
    }
}
