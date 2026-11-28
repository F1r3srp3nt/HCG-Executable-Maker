using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// HCG Server Build Host through the real New Build wizard: Inspect › Build provider = HCG Server Build Host → Build
/// step (connection test, token entry into the credential store, host, toolchains, matching project, commit, Toolchain
/// Manager) → explicit authorization → Start Build on HCG Server → waits for the owner's approval (given on HCG Server
/// by `hcg-agent build approve`, outside this driver: Executable Maker cannot approve) → results with the verified
/// provenance, host key, artifact hashes, Release Passports and native-build-request/result → pin the host key → a
/// second submission cancelled from the wizard while it awaits approval, which cancels the job on HCG Server.
/// Options: --build-host --source &lt;project folder in a Git working copy&gt; --token-file &lt;file&gt; [--expect-project id]
/// [--ops build,test,package] [--expect-remote-tool go] [--skip-cancel] --output &lt;dir&gt; --screenshots &lt;dir&gt;
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunBuildHostAsync(MainWindowViewModel main, BuildWizardViewModel wizard, string output)
    {
        _prefix = Arg("prefix", "buildhost");
        var a = wizard.Analysis!;
        Log($"Inspection: {a.Framework} · {a.Support} — {a.SupportSummary}");
        foreach (var o in wizard.BuildProviderOptions) Log($"Build provider offered: {o} — {o.Description}");
        Check(wizard.BuildProviderOptions.Select(o => o.Kind).SequenceEqual([BuildProviderKind.LocalMachine, BuildProviderKind.HcgServerBuildHost, BuildProviderKind.GitHubActions]),
            "Build Provider selector: Local Machine · HCG Server Build Host · GitHub Actions (not available yet)");
        Check(wizard.SelectedBuildProvider.Kind == BuildProviderKind.LocalMachine, "Local Machine is the default provider");
        Log($"Local Machine: can continue to Configure = {wizard.CanConfigure}");
        wizard.SelectedBuildProvider = BuildProviderCatalog.GitHubActions;
        await WaitUntil(() => wizard.SelectedBuildProvider.Kind == BuildProviderKind.LocalMachine, TimeSpan.FromSeconds(5), "unimplemented provider refused");
        Check(true, "GitHub Actions cannot be chosen yet (falls back to Local Machine)");
        wizard.SelectedBuildProvider = BuildProviderCatalog.HcgServer;
        Check(wizard.NextCommand.CanExecute(null), "HCG Server Build Host: Next is available from Inspect");
        Shot("inspect-provider");

        await wizard.NextCommand.ExecuteAsync(null);
        var panel = wizard.BuildHost;
        await WaitUntil(() => wizard.IsBuildStep && !panel.IsTesting, TimeSpan.FromMinutes(1), "Build Host connection test");
        Log($"Connection: [{panel.ConnectionTag.Text}] {panel.ConnectionMessage}");
        if (!panel.IsConnected && panel.Connection?.State == BuildHostConnectionState.NoToken)
        {
            Check(!panel.CanStart, "Without a token nothing can be submitted: " + panel.Blocker);
            Shot("token-needed");
            var tokenFile = Arg("token-file") ?? throw new InvalidOperationException("--token-file is required when no token is stored");
            panel.TokenEntry = File.ReadAllText(tokenFile).Trim();
            await panel.SaveTokenCommand.ExecuteAsync(null);
            Check(panel.TokenEntry.Length == 0, "The token field is cleared after storing (the token is never shown again)");
            Log($"Token stored in {panel.StoreDescription}; connection: [{panel.ConnectionTag.Text}] {panel.ConnectionMessage}");
        }
        Check(panel.IsConnected, "Connected to HCG Server's Build Host: " + panel.ConnectionMessage);
        if (!panel.IsConnected) { Shot("not-connected"); return 1; }
        Log("Host: " + panel.HostSummary);
        Log("Identity: " + panel.HostIdentity);
        foreach (var t in panel.Toolchains) Log($"Build Host toolchain {t.Label}: {t.Value}");
        foreach (var p in panel.Providers) Log($"Build Host provider {p.Label}: {p.Value}");
        foreach (var m in panel.Projects) Log($"Registered project: {m} — {m.Quality}: {m.Reason}");
        Log($"Source: {panel.SourceSummary} · commit {panel.Commit} · {panel.RevisionDetail}");
        var expectProject = Arg("expect-project");
        Check(panel.SelectedProject is { IsMatch: true } && (expectProject is null || panel.SelectedProject.Project.Id == expectProject),
            $"The registered project matching the imported one is selected: {panel.SelectedProject?.Project.Id} ({panel.SelectedProject?.Reason})");
        Check(panel.Source?.HasPinnedCommit == true && panel.Commit.Length == 40, $"Pinned commit from the import record: {panel.Commit}");
        Log($"Targets: {panel.Targets}");
        Log($"Provider: {panel.ProviderState}");
        Check(panel.ProviderState.Contains("available on the Build Host", StringComparison.Ordinal), "The project's provider is available on the Build Host");

        // Toolchain Manager: a tool too old or missing here, available on the Build Host.
        await WaitUntil(() => panel.Readiness.HasReport && !panel.Readiness.IsChecking, TimeSpan.FromMinutes(2), "Toolchain Manager check");
        await panel.Readiness.RefreshCommand.ExecuteAsync(null); // the person's Refresh Toolchains, after connecting
        foreach (var item in panel.Readiness.Items) Log($"Toolchain Manager: {item.Name} [{item.Tag.Text}] {item.Using}{(item.HasRemote ? " · remote: " + item.Remote : "")}");
        if (Arg("expect-remote-tool") is { } tool)
            Check(panel.Readiness.Items.Any(i => i.Name.Contains(tool, StringComparison.OrdinalIgnoreCase) && i.HasRemote && i.Remote.Contains("HCG Server Build Host", StringComparison.Ordinal)),
                $"Toolchain Manager offers the Build Host's {tool} for a requirement this computer does not meet");

        if (Arg("ops") is { } ops)
            foreach (var o in panel.Operations) o.IsSelected = ops.Split(',').Contains(o.Operation);
        Log("Operations: " + string.Join(", ", panel.Operations.Where(o => o.IsSelected).Select(o => o.Operation)));
        Check(!panel.Authorized && !panel.CanStart && panel.Blocker.Contains("Authorize", StringComparison.Ordinal), "Nothing is submitted before explicit authorization: " + panel.Blocker);
        Shot("build-host-panel");
        ShotScrolledToEnd("build-host-panel-end");
        Log("Authorization: " + panel.AuthorizationText);
        Check(panel.AuthorizationText.Contains("sends no source files", StringComparison.Ordinal) && panel.AuthorizationText.Contains(panel.Commit[..12], StringComparison.Ordinal),
            "The authorization names the commit and states that no source files are sent");
        panel.Authorized = true;
        Check(panel.CanStart, "Authorized: Start Build on HCG Server is enabled");
        Shot("authorized");

        // 1. A genuine build, approved by the owner on HCG Server.
        var run = wizard.StartBuildOnHostCommand.ExecuteAsync(null);
        await WaitUntil(() => wizard.IsAwaitingApproval || run.IsCompleted, TimeSpan.FromMinutes(2), "job awaiting the owner's approval");
        Log($"Submitted: {wizard.BuildHostJob} · {wizard.AwaitingApproval}");
        Check(wizard.IsAwaitingApproval && wizard.AwaitingApproval.Contains("hcg-agent build approve", StringComparison.Ordinal), "The wizard shows that the job waits for the owner's approval on HCG Server");
        Check(!panel.Authorized, "The authorization was used for this submission (a new build needs a new one)");
        Shot("awaiting-approval");
        await WaitUntil(() => run.IsCompleted, Budget("buildhost", 20), "Build Host build");
        await run;
        var build = wizard.Result!;
        Log($"Result: {build.Status} · {wizard.ResultDetail}");
        foreach (var line in build.ToolchainVersions.OrderBy(k => k.Key, StringComparer.Ordinal)) Log($"  {line.Key}: {line.Value}");
        foreach (var r in wizard.ProviderEvidence) Log($"Evidence {r.Label}: {r.Value}");
        Check(build.Status == BuildStatus.Succeeded, $"Build Host build succeeded{(build.FailureReason is { } why ? ": " + why : "")}");
        Check(wizard.IsResultsStep, "Results step shown");
        var outcome = wizard.LastBuildHostOutcome!;
        Check(outcome.Provenance is not null, $"Signed provenance verified (key {outcome.Provenance?.KeyFingerprint})");
        Check(outcome.Validation?.Overall == "passed", "The Build Host's validation report passed");
        Check(build.Artifacts.Count > 0 && build.Artifacts.All(x => x.Sha256.Length == 64 && x.Verification == VerificationStatus.Passed), "Every artifact verified (checksum three ways, listed in the signed provenance, validated)");
        foreach (var art in build.Artifacts) Check(File.Exists(art.Path) && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(art.Path))) == art.Sha256, $"Downloaded {art.FileName} matches SHA-256 {art.Sha256}");
        var passports = build.Artifacts.Select(x => x.Path + ".release-passport.json").Where(File.Exists).ToList();
        Check(passports.Count > 0, "Release Passport written for the distributable output: " + string.Join(", ", passports.Select(Path.GetFileName)));
        foreach (var pp in passports)
        {
            var doc = JsonNode.Parse(File.ReadAllText(pp))!;
            Check(doc["build"]?["provider"]?.GetValue<string>() == BuildProviderIds.HcgServerBuildHost && doc["build"]?["run_id"] is null,
                $"{Path.GetFileName(pp)}: build.provider {BuildProviderIds.HcgServerBuildHost}, run_id null");
        }
        var dir = Path.GetDirectoryName(build.LogPath!)!;
        foreach (var f in new[] { NativeBuildContract.RequestFileName, NativeBuildContract.ResultFileName, HcgServerBuildHost.ProvenanceFileName, HcgServerBuildHost.ValidationFileName })
            Check(File.Exists(Path.Combine(dir, f)), "Wrote " + f);
        var nativeRequest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, NativeBuildContract.RequestFileName)))!;
        Check(nativeRequest.ToJsonString().Contains(BuildProviderIds.HcgServerBuildHost, StringComparison.Ordinal), "native-build-request.json names provider hcg-server-build-host (the shared contract, no parallel protocol)");
        Check(wizard.DistributionReadiness.StartsWith("Unsigned", StringComparison.Ordinal) && !wizard.CanShipToStore, "Unsigned output is not marked distribution-ready and Ship to Store is not offered");
        Check(build.Artifacts.All(x => x.Checks.Any(c => c.Name == "Code signature" && c.Status == VerificationStatus.NotVerifiable)), "Each artifact records that it is unsigned");
        var readiness = wizard.DistributionReadiness;
        Shot("results");
        ShotScrolledToEnd("results-evidence");

        // 2. Host key: verified, then pinned.
        Log($"Host key: [{panel.KeyTag.Text}] {panel.HostKey}");
        Check(panel.KnownHost?.KeyFingerprint == outcome.Provenance?.KeyFingerprint, "The Build Host's identity is remembered from the verified provenance (device, OS/arch, isolation, key)");
        if (panel.CanPinKey)
        {
            await panel.PinKeyCommand.ExecuteAsync(null);
            Log($"Pinned: [{panel.KeyTag.Text}] {panel.HostKey}");
        }
        Check(panel.KeyTag.Text == "Pinned key verified", "Host key fingerprint pinned and verified: " + panel.HostKey);

        // 3. Cancellation from the wizard cancels the job on HCG Server.
        var cancelled = (JobId: (string?)null, State: (string?)null);
        if (!_args.ContainsKey("skip-cancel"))
        {
            wizard.RebuildWithChangesCommand.Execute(null);
            Check(wizard.IsBuildStep && wizard.ShowBuildHostPanel, "Adjust & Rebuild returns to the Build Host panel");
            Check(!panel.CanStart, "A new submission needs a new authorization: " + panel.Blocker);
            panel.Authorized = true;
            var second = wizard.StartBuildOnHostCommand.ExecuteAsync(null);
            await WaitUntil(() => wizard.IsAwaitingApproval || second.IsCompleted, TimeSpan.FromMinutes(2), "second job awaiting approval");
            var jobLine = wizard.BuildHostJob;
            Log("Second submission: " + jobLine);
            wizard.CancelBuildCommand.Execute(null);
            await WaitUntil(() => second.IsCompleted, TimeSpan.FromMinutes(2), "cancellation");
            await second;
            Check(wizard.Result?.Status == BuildStatus.Cancelled, $"Cancel Build in the wizard: {wizard.Result?.Status} — {wizard.Result?.FailureReason}");
            var jobId = wizard.LastBuildHostOutcome?.Job?.Id;
            var job = jobId is null ? null : await _services!.GetRequiredService<HcgBuildHostClient>().GetJobAsync(jobId);
            Check(job?.State == BuildJobStates.Cancelled, $"Job {jobId} is cancelled on HCG Server too ({job?.State})");
            cancelled = (jobId, job?.State);
            Shot("cancelled");
        }

        var report = new JsonObject
        {
            ["build"] = build.Id.ToString(), ["status"] = build.Status.ToString(), ["job"] = outcome.Job?.Id, ["jobState"] = outcome.Job?.State,
            ["approvedBy"] = outcome.Job?.DecidedBy, ["keyFingerprint"] = outcome.Provenance?.KeyFingerprint, ["keyPinned"] = panel.KeyTag.Text,
            ["host"] = outcome.Provenance is { } pv ? $"{pv.Document.BuildHost.Name} {pv.Document.BuildHost.Os}/{pv.Document.BuildHost.Architecture} hcg-agent {pv.Document.BuildHost.AgentVersion}" : null,
            ["isolation"] = outcome.Provenance?.Document.Isolation.Backend,
            ["source"] = outcome.Evidence is { } ev ? $"{ev.Repository}@{ev.Commit}" : null, ["toolchain"] = outcome.Evidence?.Toolchain,
            ["artifacts"] = new JsonArray(build.Artifacts.Select(x => (JsonNode?)new JsonObject { ["name"] = x.FileName, ["size"] = x.SizeBytes, ["sha256"] = x.Sha256, ["verification"] = x.Verification.ToString() }).ToArray()),
            ["passports"] = new JsonArray(passports.Select(p => (JsonNode?)p).ToArray()),
            ["buildDirectory"] = dir, ["distributionReadiness"] = readiness,
            ["cancelledJob"] = cancelled.JobId, ["cancelledJobState"] = cancelled.State,
        };
        File.WriteAllText(Path.Combine(output, "buildhost-ui-report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Log("Report: " + Path.Combine(output, "buildhost-ui-report.json"));
        return Failures.Count == 0 ? 0 : 1;
    }
}
