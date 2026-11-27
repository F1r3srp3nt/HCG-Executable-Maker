using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --build-host …</c>: HCG Server's Build Host (<c>hcg.buildhost/1</c>) without the UI.
/// <list type="bullet">
/// <item><c>status</c>: contract, isolation, toolchains, providers and registered projects.</item>
/// <item><c>jobs</c>: this client's jobs.</item>
/// <item><c>token set</c> reads the build-client token from standard input and stores it in the OS credential store;
/// <c>token forget</c> removes it. The token is never printed or written to a file.</item>
/// <item><c>run --project &lt;id&gt; --commit &lt;40-hex&gt; --ops build[,test,package] [--products a,b] [--targets a,b]
/// [--configuration debug|release] [--purpose "…"] [--artifact &lt;name&gt;]… [--maker-project &lt;guid&gt;] [--out &lt;file.json&gt;]</c>:
/// submits the job, shows that it waits for the owner's approval on HCG Server, follows it, downloads and verifies the
/// artifacts, verifies the signed provenance and prints the HCG Repo evidence. Exit 0 only when the build succeeded and
/// everything verified.</item>
/// </list>
/// Common options: <c>--url &lt;loopback base URL&gt;</c> (or HCG_BUILDHOST_URL), <c>--pin &lt;key fingerprint&gt;</c> (or
/// HCG_BUILDHOST_KEY_FINGERPRINT), and <c>--token-stdin</c> to read the token from standard input for this run only
/// (kept in memory). Executable Maker never approves a job.
/// </summary>
internal static class BuildHostCommand
{
    public const string UrlVariable = "HCG_BUILDHOST_URL";
    public const string PinVariable = "HCG_BUILDHOST_KEY_FINGERPRINT";

    /// <summary>Base URL and key pin from the environment (non-secret settings); defaults to the local Build Host.</summary>
    public static HcgBuildHostSettings SettingsFromEnvironment(string? url = null, string? pin = null)
    {
        url ??= Environment.GetEnvironmentVariable(UrlVariable);
        pin ??= Environment.GetEnvironmentVariable(PinVariable);
        return new HcgBuildHostSettings
        {
            BaseUrl = string.IsNullOrWhiteSpace(url) ? HcgBuildHostSettings.DefaultBaseUrl : new Uri(url),
            ExpectedKeyFingerprint = string.IsNullOrWhiteSpace(pin) ? null : pin,
        };
    }

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IReadOnlyList<string>? List(string name) => Arg(name) is { Length: > 0 } v ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var verb = Arg("--build-host") ?? "status";

        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        HcgBuildHostSettings settings;
        try { settings = SettingsFromEnvironment(Arg("--url"), Arg("--pin")); HcgBuildHostClient.ValidateBaseUrl(settings.BaseUrl); }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException) { Console.Error.WriteLine(ex.Message); return 2; }

        BuildHostCredentials credentials;
        if (args.Contains("--token-stdin"))
        {
            var memory = new SessionOnlyCredentialStore();
            credentials = new BuildHostCredentials(memory);
            try { credentials.SaveAsync(Console.In.ReadToEnd()).GetAwaiter().GetResult(); }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }
        else credentials = services.GetRequiredService<BuildHostCredentials>();
        var client = new HcgBuildHostClient(HcgBuildHostClient.CreateHttpClient(), credentials, settings);

        Console.WriteLine(HostInfo.BuildIdentity);
        Console.WriteLine($"Build Host: {client.BaseUrl} · token: {credentials.StoreDescription}{(settings.ExpectedKeyFingerprint is { } p ? " · pinned key " + HcgBuildHostClient.NormalizeFingerprint(p) : "")}");
        try
        {
            return verb switch
            {
                "status" => Status(client),
                "jobs" => Jobs(client),
                "token" => Token(Arg("token"), services.GetRequiredService<BuildHostCredentials>()),
                "run" => RunJob(client, services, paths, Arg, List, args),
                _ => Usage(),
            };
        }
        catch (HcgBuildHostException ex)
        {
            Console.Error.WriteLine($"{ex.Failure}: {ex.Message}");
            return ex.Failure switch { BuildHostFailure.NotConfigured or BuildHostFailure.Unauthorized => 3, BuildHostFailure.ContractMismatch => 4, BuildHostFailure.Unavailable => 9, _ => 1 };
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: --build-host status | jobs | token set|forget | run --project <id> --commit <40-hex> --ops build[,test,package] [--products a,b] [--targets a,b] [--configuration debug|release] [--purpose \"…\"] [--artifact <name>] [--maker-project <guid>] [--out <file.json>] [--url <loopback URL>] [--pin <fingerprint>] [--token-stdin]");
        return 2;
    }

    private static int Status(HcgBuildHostClient client)
    {
        var s = client.GetStatusAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Contract: {s.Contract} · isolation {s.Isolation}{(s.Enforced ? " (enforced)" : " (NOT enforced)")} · queued {s.Queued}{(s.Running is { } r ? " · running " + r : "")}");
        foreach (var t in s.Toolchains) Console.WriteLine($"  toolchain {t.Provider}/{t.Tool}: {t.Version} ({t.Path})");
        foreach (var p in s.Providers) Console.WriteLine($"  provider {p.Id}: {(p.Available ? "available" : "unavailable")} — {p.Detail}");
        foreach (var p in client.GetProjectsAsync().GetAwaiter().GetResult())
            Console.WriteLine($"  project {p.Id}: {p.Name} · {p.Source.Repository} · {p.Provider} · operations {string.Join(",", p.Operations)}");
        return 0;
    }

    private static int Jobs(HcgBuildHostClient client)
    {
        foreach (var j in client.ListJobsAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine($"{j.Id} {j.State} {j.Project.Id} {j.Request?.Commit} {j.Detail}");
            if (j.State == BuildJobStates.AwaitingApproval) Console.WriteLine("  " + HcgBuildHostClient.ApprovalInstruction(j.Id));
        }
        return 0;
    }

    private static int Token(string? action, BuildHostCredentials stored)
    {
        switch (action)
        {
            case "set":
                Console.WriteLine("Paste the token printed by `hcg-agent build client add executable-maker`, then end input (Ctrl-D):");
                stored.SaveAsync(Console.In.ReadToEnd()).GetAwaiter().GetResult();
                Console.WriteLine($"Stored in {stored.StoreDescription}.{(stored.IsPersistent ? "" : " This store keeps it for this run only (no OS credential store was found); use --token-stdin per run.")}");
                return 0;
            case "forget":
                stored.ForgetAsync().GetAwaiter().GetResult();
                Console.WriteLine("Removed the Build Host client token.");
                return 0;
            default:
                return Usage();
        }
    }

    private static int RunJob(HcgBuildHostClient client, ServiceProvider services, IAppPaths paths, Func<string, string?> arg, Func<string, IReadOnlyList<string>?> list, string[] args)
    {
        if (arg("--project") is not { } projectId || arg("--commit") is not { } commit || list("--ops") is not { Count: > 0 } ops) return Usage();
        var artifacts = args.Select((a, i) => (a, i)).Where(x => x.a == "--artifact" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();

        // A BuildRecord is persisted only for an existing Executable Maker project (builds belong to a project).
        var builds = services.GetRequiredService<IBuildRepository>();
        ProjectRecord? makerProject = null;
        if (arg("--maker-project") is { } mp && Guid.TryParse(mp, out var mpId))
            makerProject = services.GetRequiredService<IProjectRepository>().GetAsync(mpId).GetAwaiter().GetResult();
        var provider = new HcgServerBuildHost(client, paths, TimeProvider.System, makerProject is null ? null : builds);
        var name = makerProject?.Name ?? projectId;
        var request = new BuildHostBuildRequest
        {
            ProjectId = makerProject?.Id ?? Guid.Empty, ProjectName = name,
            Settings = new BuildSettings
            {
                ProjectRelativePath = ".", Platform = HostInfo.CurrentPlatform, Architecture = HostInfo.CurrentArchitecture,
                OutputDirectory = paths.DefaultOutputDirectory,
                Identity = new AppIdentity { ApplicationName = name, Version = "0.0.0", ApplicationId = "hcg.buildhost." + projectId },
            },
            BuildHostProjectId = projectId, Commit = commit, Operations = ops,
            Options = new BuildHostOptions { Products = list("--products"), Targets = list("--targets"), Configuration = arg("--configuration") },
            Purpose = arg("--purpose") ?? $"HCG Executable Maker build of {projectId} at {commit[..Math.Min(12, commit.Length)]}",
            Artifacts = artifacts.Count > 0 ? artifacts : null,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var outcome = provider.RunAsync(request, new ConsoleObserver(), cts.Token).GetAwaiter().GetResult();
        var b = outcome.Build;
        Console.WriteLine();
        Console.WriteLine($"Build {b.Id}: {b.Status}{(b.FailureReason is null ? "" : " — " + b.FailureReason)} · job {outcome.Job?.Id} ({outcome.Job?.State}) · log {b.LogPath}");
        foreach (var kv in b.ToolchainVersions.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        foreach (var a in b.Artifacts)
        {
            Console.WriteLine($"  artifact {a.FileName}: {a.SizeBytes:N0} bytes · SHA-256 {a.Sha256} · {a.Verification}");
            foreach (var c in a.Checks) Console.WriteLine($"    {c.Name}: {c.Status} — {c.Detail}");
        }
        // The evidence Unleashify's submission.json takes (PackageProvenance.FromBuildHost): provider, source, toolchain, builtAt; no CI run.
        var evidence = outcome.Evidence is { } ev ? new JsonObject
        {
            ["provider"] = ev.Provider, ["repository"] = ev.Repository, ["commit"] = ev.Commit, ["dirty"] = ev.Dirty,
            ["toolchain"] = ev.Toolchain, ["builtAt"] = ev.BuiltAt, ["job"] = ev.JobId, ["keyFingerprint"] = ev.KeyFingerprint, ["approvedBy"] = ev.ApprovedBy,
        } : null;
        Console.WriteLine(evidence is null ? $"HCG Repo evidence: none ({outcome.EvidenceProblem})" : "HCG Repo evidence (no CI run):\n" + evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var buildDir = Path.GetDirectoryName(b.LogPath!)!;
        var nativeFiles = new[] { NativeBuildContract.RequestFileName, NativeBuildContract.ResultFileName, HcgServerBuildHost.ProvenanceFileName, HcgServerBuildHost.ValidationFileName }
            .Select(f => Path.Combine(buildDir, f)).Where(File.Exists).ToList();
        foreach (var f in nativeFiles) Console.WriteLine("  wrote " + f);

        if (arg("--out") is { } outFile)
        {
            var report = new JsonObject
            {
                ["build"] = new JsonObject
                {
                    ["id"] = b.Id.ToString(), ["status"] = b.Status.ToString(), ["failureReason"] = b.FailureReason, ["exitCode"] = b.ExitCode,
                    ["startedAt"] = b.StartedAt.ToString("O"), ["endedAt"] = b.EndedAt?.ToString("O"), ["log"] = b.LogPath,
                    ["toolchainVersions"] = new JsonObject(b.ToolchainVersions.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    ["artifacts"] = new JsonArray(b.Artifacts.Select(a => (JsonNode?)new JsonObject
                    {
                        ["file"] = a.Path, ["format"] = a.Format.ToString(), ["size"] = a.SizeBytes, ["sha256"] = a.Sha256, ["verification"] = a.Verification.ToString(),
                        ["checks"] = new JsonArray(a.Checks.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail }).ToArray()),
                    }).ToArray()),
                },
                ["job"] = outcome.Job?.Id, ["jobState"] = outcome.Job?.State,
                ["provenance"] = outcome.Provenance is { } pv ? new JsonObject { ["verified"] = true, ["keyFingerprint"] = pv.KeyFingerprint, ["issuedAt"] = pv.Document.IssuedAt.ToString("O") } : null,
                ["validation"] = outcome.Validation?.Overall,
                ["evidence"] = evidence,
                ["files"] = new JsonArray(nativeFiles.Select(f => (JsonNode?)f).ToArray()),
                ["evidenceProblem"] = outcome.EvidenceProblem,
            };
            File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine("Report: " + outFile);
        }
        return b.Status == BuildStatus.Succeeded && outcome.Evidence is not null ? 0 : 1;
    }

    private sealed class ConsoleObserver : IBuildHostObserver
    {
        public void OnStage(BuildStage stage, string description) => Console.WriteLine($"== {stage}: {description}");
        public void OnLog(BuildLogLine line) { if (line.Kind != BuildLogKind.Output) Console.WriteLine($"[{line.Kind}] {line.Text}"); }
        public void OnAwaitingApproval(BuildJob job, string instruction) => Console.WriteLine($"*** {instruction} Executable Maker does not approve jobs.");
    }
}
