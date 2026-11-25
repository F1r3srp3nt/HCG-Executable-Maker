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
              [--owner-approval "<who> on <date>: '<their words>'"] (the owner's recorded approval of this version as a free public candidate demo; without it the handoff is not importable)
              [--approved-sha256 <sha>] (the exact .deb the owner approved; required for a local build's approval to count)
              [--local --source-visibility private|public [--host <label>]] (built on this computer: no CI run or artifact; exports HCG Repo's local-file request and hcg-repo/)
              [--hcg-repo <HCG Repo checkout>] (preview the import with HCG Repo's own importer, dry run on a throwaway copy)
              [--icon-artwork <png>] (approved fallback when the app's own icon is missing or unusable; CgBI icons are converted)
              [--timestamp <ISO 8601>] [--no-handoff] [--no-lifecycle]
          HCGExecutableMaker --ship-to-repo submit  --hcg-repo <checkout> --handoff <folder> [--incoming <store>]
          HCGExecutableMaker --ship-to-repo show    --hcg-repo <checkout> --handoff <folder> | --id <submission> [--incoming <store>]
          HCGExecutableMaker --ship-to-repo approve --hcg-repo <checkout> --handoff <folder> --approved-sha256 <sha> --by "<name>" --record "<approval, verbatim>"
          HCGExecutableMaker --ship-to-repo prepare --hcg-repo <checkout> --handoff <folder> [--free-demo-approval "<owner's words>"] [--create-package-metadata]
              (HCG Repo's Release Manager; nothing is committed, pushed or published)
          HCGExecutableMaker --rootless-deb-validate <file.deb> [--package <id>] [--package-version <v>] [--bundle-id <id>] [--device-ios <x.y>] [--published <version>=<sha256>]...
        """;

    /// <summary>
    /// <c>--ship-to-repo</c>: HCG Repo's Candidate Release Manager for an exported handoff (submit → show → approve → prepare).
    /// Exit codes: 0 done, 2 usage, 1 refused by HCG Repo or by Maker (the output says why).
    /// </summary>
    public static int ShipToRepoCli(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        Console.WriteLine(HostInfo.BuildIdentity);
        var action = Arg("--ship-to-repo");
        if (Arg("--hcg-repo") is not { } checkout || action is not ("submit" or "show" or "approve" or "prepare")) { Console.Error.WriteLine(Usage); return 2; }
        checkout = Path.GetFullPath(checkout);
        if (new ExecutableLocator().Find("python3") is not { } python) { Console.WriteLine("python3 not found (HCG Repo needs Python 3.11+ and dpkg-deb)."); return 1; }
        var incoming = Arg("--incoming");
        var handoff = Arg("--handoff") is { } h ? Path.GetFullPath(h) : null;
        var id = Arg("--id");
        if (id is null && handoff is not null && File.Exists(Path.Combine(handoff, "provenance.json")))
        {
            var prov = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(handoff, "provenance.json")))!;
            id = ShipToRepo.SubmissionId(prov["package"]!.GetValue<string>(), prov["version"]!.GetValue<string>(), prov["architecture"]!.GetValue<string>(), prov["sha256"]!.GetValue<string>());
        }
        var runner = new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner();
        ShipToRepo.Step step = action switch
        {
            "submit" when handoff is not null => ShipToRepo.SubmitAsync(runner, python, checkout, handoff, incoming, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult(),
            "show" when id is not null => ShipToRepo.ShowAsync(runner, python, checkout, id, incoming, CancellationToken.None).GetAwaiter().GetResult(),
            "approve" when id is not null => ShipToRepo.ApproveAsync(runner, python, checkout, id, Arg("--approved-sha256") ?? "", Arg("--by") ?? "", Arg("--record") ?? "", incoming, CancellationToken.None).GetAwaiter().GetResult(),
            "prepare" when id is not null => ShipToRepo.PrepareAsync(runner, python, checkout, id, Arg("--free-demo-approval"), args.Contains("--create-package-metadata"), incoming, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult(),
            _ => new ShipToRepo.Step(ShipToRepo.Action.Show, false, 2, "Name the handoff (--handoff) or the submission (--id).", null, null, null),
        };
        if (action is "show" or "approve") Console.WriteLine(step.Output.Trim());
        if (step.SubmissionId is { } sid) Console.WriteLine($"Submission: {sid}{(step.State is { } st ? $" ({st})" : "")}");
        if (action == "prepare" && step.Succeeded) Console.WriteLine("Prepared in the checkout only. Review, commit on a branch and open a pull request; publication is HCG Repo's manual Publish workflow.");
        return step.ExitCode == 2 ? 2 : step.Succeeded ? 0 : 1;
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
                Console.WriteLine($"Candidate handoff: {h.Folder} ({(h.ImportRequestComplete ? local ? "local-file request pinned; HCG Repo files in hcg-repo/" : "import request pinned" : "import request INCOMPLETE: source not pinned")})");
                foreach (var m in h.Missing) Console.WriteLine("  Missing: " + m);
                if (Arg("--hcg-repo") is { } checkout)
                {
                    var python = new ExecutableLocator().Find("python3");
                    if (python is null) { Console.WriteLine("Import preview: python3 not found"); return 7; }
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
