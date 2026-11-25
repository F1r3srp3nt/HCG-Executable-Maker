using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>--rootless-deb</c>: the jailbreak app packaging workflow without the UI (the same <see cref="RootlessAppWorkflow"/>
/// the Rootless package page uses). Inspect the app a build produced → package it as a rootless .deb → validate →
/// export the HCG Repo candidate handoff. <c>--rootless-deb-validate</c> validates an existing .deb only.
/// Exit codes: 0 done, 2 usage, 4 inspection failed or a prerequisite is missing, 5 packaging failed, 6 validation or lifecycle failed,
/// 7 the HCG Repo import preview rejected the package.
/// Nothing is installed, uploaded or published.
/// </summary>
internal static class RootlessDebCommand
{
    public const string Usage = """
        Usage:
          HCGExecutableMaker --rootless-deb <app|xcarchive|ipa> --package <id> --package-version <debian-version> --name <display name>
              [--maintainer <text, default Hakmi Capital Group>] --description <text> [--out <folder>] [--section <section>] [--depends <dep>]...
              [--entitlements <the project's .entitlements>] [--ldid <path>] [--device-ios <x.y>] [--published <version>=<sha256>]...
              [--repository <owner/name> --commit <40-char sha> [--dirty] [--ci-run <id> --ci-artifact <name> --ci-run-url <url> [--ci-repository <owner/name> --ci-commit <sha the run built>]] [--built-with <text>]]
              [--summary <text>] [--change <text>]... [--known-issue <text>]... [--production-access public|connect|paid|undecided] [--device-target <text>]
              [--owner-approval "<who> on <date>: '<their words>'"] (CI builds: the owner's recorded approval of this version as a free public candidate demo; without it the handoff is not importable. Local builds: ignored — the owner approves in HCG Repo with release approve/prepare)
              [--approved-sha256 <sha>] (CI builds: the exact .deb the owner approved)
              [--local --source-visibility private|public [--host <label>]] (built on this computer: no CI run or artifact; exports HCG Repo's local-file request and hcg-repo/)
              [--hcg-repo <HCG Repo checkout>] (preview the import with HCG Repo's own importer, dry run on a throwaway copy)
              [--icon-artwork <png>] (approved fallback when the app's own icon is missing or unusable; CgBI icons are converted)
              [--timestamp <ISO 8601>] [--no-handoff] [--no-lifecycle]
          HCGExecutableMaker --ship-to-repo submit --hcg-repo <checkout> --handoff <folder> [--incoming <store>]
          HCGExecutableMaker --ship-to-repo status|show --hcg-repo <checkout> --handoff <folder> | --id <submission> [--incoming <store>]
              (HCG Repo's Release Manager: submit validates into the private incoming store; status is HCG Repo's result with the
               six evidence levels. Approval, preparation and publication are the owner's: Maker prints their commands, never runs them)
          HCGExecutableMaker --rootless-deb-validate <file.deb> [--package <id>] [--package-version <v>] [--bundle-id <id>] [--device-ios <x.y>] [--published <version>=<sha256>]...
        """;

    /// <summary>
    /// <c>--ship-to-repo</c>: HCG Repo's Candidate Release Manager for an exported handoff, per HCG Repo's Executable Maker
    /// submission contract: submit (with <c>--result-json</c>), status (JSON), show (HCG Repo's review text). It prints the six
    /// levels separately and the owner's next commands; it never approves, prepares or publishes.
    /// Exit codes: 0 validated (submit) or read (status/show), 1 refused by HCG Repo, 2 usage.
    /// </summary>
    public static int ShipToRepoCli(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        Console.WriteLine(HostInfo.BuildIdentity);
        var action = Arg("--ship-to-repo");
        if (action is "approve" or "prepare")
        {
            Console.Error.WriteLine($"HCG Executable Maker does not run `release {action}`: under HCG Repo's submission contract only the owner approves the exact SHA-256 and prepares the Candidate. " +
                                    "Run `--ship-to-repo status` for the exact commands to run in the HCG Repo checkout.");
            return 2;
        }
        if (Arg("--hcg-repo") is not { } checkout || action is not ("submit" or "status" or "show")) { Console.Error.WriteLine(Usage); return 2; }
        checkout = Path.GetFullPath(checkout);
        if (new ExecutableLocator().Find("python3") is not { } python) { Console.WriteLine("python3 not found (HCG Repo needs Python 3.11+ and dpkg-deb)."); return 1; }
        var incoming = Arg("--incoming");
        var handoff = Arg("--handoff") is { } h ? Path.GetFullPath(h) : null;
        var id = Arg("--id") ?? (handoff is null ? null : SubmissionIdOf(handoff));
        var runner = new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner();
        ShipToRepo.Step step = action switch
        {
            "submit" when handoff is not null => ShipToRepo.SubmitAsync(runner, python, checkout, handoff, incoming, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult(),
            "status" when id is not null => ShipToRepo.StatusAsync(runner, python, checkout, id, incoming, CancellationToken.None).GetAwaiter().GetResult(),
            "show" when id is not null => ShipToRepo.ShowAsync(runner, python, checkout, id, incoming, CancellationToken.None).GetAwaiter().GetResult(),
            _ => new ShipToRepo.Step(ShipToRepo.Action.Status, false, 2, "Name the handoff (--handoff) or the submission (--id).", null, null, null),
        };
        if (action == "show" || (action == "status" && step.Result is null)) Console.WriteLine(step.Output.Trim());
        if (step.Result is { } result)
        {
            Console.WriteLine();
            Console.Write(ShipToRepo.Summary(result, ShipToRepo.DraftAccessLevel(handoff)));
        }
        else if (step.SubmissionId is { } sid) Console.WriteLine($"Submission: {sid}{(step.State is { } st ? $" ({st})" : "")}");
        return step.ExitCode == 2 ? 2 : step.Succeeded ? 0 : 1;
    }

    /// <summary>HCG Repo's submission id for a handoff folder, from its submission.json (or a legacy provenance.json).</summary>
    private static string? SubmissionIdOf(string handoff)
    {
        try
        {
            if (File.Exists(Path.Combine(handoff, SubmissionManifest.FileName)) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(handoff, SubmissionManifest.FileName))) is { } m)
                return ShipToRepo.SubmissionId(m["package"]!["identifier"]!.GetValue<string>(), m["package"]!["version"]!.GetValue<string>(), m["package"]!["architecture"]!.GetValue<string>(), m["artifact"]!["sha256"]!.GetValue<string>());
            if (File.Exists(Path.Combine(handoff, "provenance.json")) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(handoff, "provenance.json"))) is { } p)
                return ShipToRepo.SubmissionId(p["package"]!.GetValue<string>(), p["version"]!.GetValue<string>(), p["architecture"]!.GetValue<string>(), p["sha256"]!.GetValue<string>());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NullReferenceException or InvalidOperationException or IOException) { }
        return null;
    }

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        List<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();
        List<(string, string)> Published() => All("--published").Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        Console.WriteLine(HostInfo.BuildIdentity);

        if (args.Contains("--rootless-deb-validate"))
        {
            var deb = Arg("--rootless-deb-validate");
            if (deb is null || !File.Exists(deb)) { Console.Error.WriteLine(Usage); return 2; }
            var v = RootlessAppDebValidator.ValidateAsync(deb, new(Arg("--package"), Arg("--package-version"), Arg("--bundle-id"), Arg("--device-ios"), Published()), CancellationToken.None).GetAwaiter().GetResult();
            Console.Write(v.Report);
            Console.WriteLine(v.Passed ? "Validation: passed" : "Validation: FAILED");
            return v.Passed ? 0 : 6;
        }

        var source = Arg("--rootless-deb");
        string? pkgId = Arg("--package"), version = Arg("--package-version"), name = Arg("--name"), maintainer = Arg("--maintainer") ?? RootlessAppPackager.HcgMaintainer, description = Arg("--description");
        if (source is null || pkgId is null || version is null || name is null || maintainer is null || description is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var ldid = Arg("--ldid");
        if (ldid is null)
        {
            var report = services.GetRequiredService<IToolchainDetector>().DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
            ldid = report.Find(ToolchainIds.Ldid) is { IsAvailable: true, ExecutablePath: { } found } ? found : null;
        }
        Console.WriteLine("ldid: " + (ldid ?? "not found (needed only if the build's Mach-O files are unsigned)"));
        DateTimeOffset? stamp = Arg("--timestamp") is { } ts && DateTimeOffset.TryParse(ts, out var t) ? t : null;
        var spec = new RootlessAppPackageSpec(pkgId, version, name, maintainer, description.Replace("\\n", "\n"), Arg("--section") ?? "Applications",
            ExtraDepends: All("--depends"), EntitlementsFile: Arg("--entitlements"), Timestamp: stamp);
        var local = args.Contains("--local");
        if (local && (Arg("--ci-run") is not null || Arg("--source-visibility") is not ("private" or "public")))
        {
            Console.Error.WriteLine("--local needs --source-visibility private|public and no --ci-run (a local build has no run or artifact).");
            return 2;
        }
        var prov = new PackageProvenance(Arg("--repository"), Arg("--commit"), args.Contains("--dirty") ? true : Arg("--commit") is null ? null : false,
            long.TryParse(Arg("--ci-run"), out var run) ? run : null, Arg("--ci-artifact"), Arg("--ci-run-url"), Arg("--built-with"), HostInfo.BuildIdentity,
            Arg("--ci-repository"), Arg("--ci-commit"),
            local ? new LocalBuildInfo($"{HostInfo.BuildIdentity} · Unleashify, local", null, stamp ?? DateTimeOffset.UtcNow, Arg("--host") ?? LocalBuild.DefaultHostLabel(), Arg("--source-visibility")!, null) : null);
        var texts = new CandidateTexts(Arg("--summary") ?? description.Split('\n')[0], All("--change"), All("--known-issue"), Arg("--production-access") ?? "undecided", Arg("--device-target"), Arg("--owner-approval"), Arg("--approved-sha256")?.ToLowerInvariant(), Arg("--icon-artwork"));
        var outDir = Path.GetFullPath(Arg("--out") ?? Path.Combine(Environment.CurrentDirectory, "rootless-deb"));

        var workflow = new RootlessAppWorkflow(services.GetRequiredService<IProcessRunner>(), TimeProvider.System);
        var outcome = workflow.RunAsync(new RootlessAppWorkflow.Request(source, outDir, spec, prov, texts, ldid, Arg("--device-ios"), Published(), !args.Contains("--no-handoff"), SimulateLifecycle: !args.Contains("--no-lifecycle")),
            (stage, line) => Console.WriteLine($"[{stage}] {line}"), CancellationToken.None).GetAwaiter().GetResult();
        if (outcome.Succeeded)
        {
            var f = outcome.Validation!.Facts;
            Console.WriteLine($"Package: {outcome.Package!.DebPath}");
            Console.WriteLine($"SHA-256: {f.Sha256}  Size: {f.Size:N0} bytes");
            if (outcome.Lifecycle is { } life) Console.WriteLine("Lifecycle: " + life.Summary);
            if (outcome.Provenance?.Local is { } lb) Console.WriteLine($"Local build: {lb.Toolchain ?? "toolchain not recorded in the app"} · {lb.Host} · {lb.SourceVisibility} source · no CI run or artifact");
            if (outcome.Handoff is { } h)
            {
                Console.WriteLine($"Candidate handoff: {h.Folder} ({(h.ImportRequestComplete ? local ? "submission.json ready for Ship to Repo" : "import request pinned" : "import request INCOMPLETE: source not pinned")})");
                foreach (var m in h.Missing) Console.WriteLine("  Missing: " + m);
                if (Arg("--hcg-repo") is { } checkout)
                {
                    var python = new ExecutableLocator().Find("python3");
                    if (python is null) { Console.WriteLine("Import preview: python3 not found"); return 7; }
                    if (local)
                    {
                        // A local build: HCG Repo's Release Manager reviews it in a throwaway incoming store (nothing is kept).
                        var review = ShipToRepo.PreviewAsync(services.GetRequiredService<IProcessRunner>(), python, Path.GetFullPath(checkout), h.Folder, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine("Import preview: HCG Repo Release Manager review: " + (review.State ?? "not run").ToUpperInvariant());
                        if (review.Result is { } levels) Console.Write(ShipToRepo.RenderLevels(levels));
                        return review.State == "validated" ? 0 : 7;
                    }
                    var genuine = texts.OwnerApproval is not null && !h.Missing.Any(m => m.StartsWith("owner approval", StringComparison.Ordinal));
                    var preview = HcgRepoImportPreview.RunAsync(services.GetRequiredService<IProcessRunner>(), python, Path.GetFullPath(checkout), h.Folder,
                        hypotheticalApproval: !genuine, requireGenuineApproval: genuine, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("Import preview: " + preview.Verdict);
                    if (preview.ReportJson is { } json) File.WriteAllText(Path.Combine(outDir, "hcgrepo-import-preview.json"), json);
                    if (preview.Verdict.StartsWith("REJECTED", StringComparison.Ordinal) || !preview.Ran) return 7;
                }
            }
            return 0;
        }
        Console.WriteLine($"Stopped at {outcome.ReachedStage}: {outcome.Failure}");
        return outcome.ReachedStage switch { RootlessAppWorkflow.Stage.Inspect => 4, RootlessAppWorkflow.Stage.Package => 5, _ => 6 };
    }
}
