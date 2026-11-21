using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.BuildEngine.Doctor;

public sealed record DoctorInput(ProjectRecord Project, MobileAnalysis Mobile, MobileOutput Output, MobileBuildOptions? Options, ToolchainReport? Report);

/// <summary>
/// Build Doctor: inspects an imported mobile project for everything that stops the selected build — project references,
/// MSBuild properties and imports, source files, import scope (monorepos), SDK, target framework, workload, Xcode,
/// packages, runtime identifier, bundle identifier, signing and provisioning — and explains each finding with its root
/// cause, the affected file and a repair. Diagnosis is read-only: it reads project files statically (MSBuild-style
/// evaluation of properties, conditions and imports, no targets or tasks) and reuses the toolchain report. Repairs are
/// proposed here and applied by <see cref="DoctorRepairs"/> only after approval.
/// </summary>
public static partial class BuildDoctor
{
    public static DoctorReport Diagnose(DoctorInput input)
    {
        var findings = new List<DoctorFinding>();
        var facts = new List<string>();
        var root = Path.GetFullPath(input.Project.SourceDirectory);
        var m = input.Mobile;
        facts.Add($"Project {input.Project.Name}: {m.Framework.DisplayName()} → {input.Output.DisplayName()}; workspace source {root}");
        if (m.DotNetIos is { } dn)
        {
            DotNetGraph(input, root, dn.ProjectPath, findings, facts);
            DotNetIosChecks(input, root, dn, findings, facts);
        }
        Readiness(input, findings, facts);

        var ordered = findings
            .GroupBy(f => f.Id).Select(g => g.First())
            .OrderBy(f => f.Severity).ThenBy(f => f.Category).ThenBy(f => f.AffectedFile, StringComparer.Ordinal)
            .ToList();
        var subject = m.DotNetIos?.ApplicationTitle ?? input.Project.Name;
        return new DoctorReport(subject, input.Output, DateTimeOffset.UtcNow, ordered, facts);
    }

    /// <summary>Readiness in a few words, for the summary header.</summary>
    public static string Readiness(DoctorReport r) =>
        r.Findings.Any(f => f is { Severity: DoctorSeverity.Error, Category: DoctorCategory.Host }) ? "Needs a Mac with Xcode"
        : r.Errors > 0 ? "Configuration required"
        : r.Warnings > 0 ? "Ready to build (with warnings)"
        : "Ready to build";

    /// <summary>"3 unresolved project references", "1 missing workload"… one line per category with errors or warnings.</summary>
    public static IReadOnlyList<string> Summary(DoctorReport r) =>
        r.Findings.Where(f => f.Severity != DoctorSeverity.Info).GroupBy(f => f.Category)
            .Select(g => $"{g.Count()} {Noun(g.Key, g.Count())}").ToList();

    private static string Noun(DoctorCategory c, int n) => c switch
    {
        DoctorCategory.ImportScope => n == 1 ? "project or file outside the imported folder" : "projects or files outside the imported folder",
        DoctorCategory.ProjectReferences => n == 1 ? "unresolved project reference" : "unresolved project references",
        DoctorCategory.MsBuildProperties => n == 1 ? "unresolved MSBuild property" : "unresolved MSBuild properties",
        DoctorCategory.Imports => n == 1 ? "missing MSBuild import" : "missing MSBuild imports",
        DoctorCategory.SourceFiles => n == 1 ? "missing source file" : "missing source files",
        DoctorCategory.Sdk => ".NET SDK issue" + (n == 1 ? "" : "s"),
        DoctorCategory.TargetFramework => "unsupported target framework" + (n == 1 ? "" : "s"),
        DoctorCategory.Workload => n == 1 ? "missing or mismatched workload" : "missing or mismatched workloads",
        DoctorCategory.Xcode => "Xcode issue" + (n == 1 ? "" : "s"),
        DoctorCategory.Packages => "NuGet package issue" + (n == 1 ? "" : "s"),
        DoctorCategory.RuntimeIdentifier => "runtime identifier issue" + (n == 1 ? "" : "s"),
        DoctorCategory.BundleIdentifier => "bundle identifier issue" + (n == 1 ? "" : "s"),
        DoctorCategory.Signing => "signing requirement" + (n == 1 ? "" : "s"),
        DoctorCategory.Provisioning => "provisioning requirement" + (n == 1 ? "" : "s"),
        DoctorCategory.Configuration => "configuration issue" + (n == 1 ? "" : "s"),
        DoctorCategory.Architecture => "architecture issue" + (n == 1 ? "" : "s"),
        DoctorCategory.Host => "host requirement" + (n == 1 ? "" : "s"),
        _ => "build input" + (n == 1 ? "" : "s"),
    };

    // ───────────── Project graph: references, properties, imports, files ─────────────

    private static void DotNetGraph(DoctorInput input, string root, string headRel, List<DoctorFinding> findings, List<string> facts)
    {
        var queue = new Queue<string>([Path.GetFullPath(Path.Combine(root, headRel))]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outside = new List<(string From, string Raw, string Target, string Kind)>();
        var environment = new SortedSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0 && seen.Count < 300)
        {
            var full = queue.Dequeue();
            if (!seen.Add(full)) continue;
            var rel = Rel(root, full);
            var dir = Path.GetDirectoryName(full)!;
            var f = DotNetProjectInspector.LoadProject(root, full);
            if (f is null)
            {
                findings.Add(new($"xml:{rel}", DoctorCategory.ProjectReferences, DoctorSeverity.Error, $"{rel} is not a valid MSBuild project file.",
                    "The file is not well-formed XML.", rel, "Fix the XML in the project (the build cannot read it)."));
                continue;
            }
            facts.Add($"{rel}: {f.RawProjectReferences.Count} project reference(s), {f.Imports.Count} import(s) followed" +
                      ((f.Get("TargetFramework") ?? f.Get("TargetFrameworks")) is { } tf ? $", {tf}" : ""));
            foreach (var name in f.EnvironmentInputs) environment.Add(name);
            foreach (var missingImport in f.MissingImports)
                findings.Add(new($"import:{rel}:{Rel(root, missingImport)}", DoctorCategory.Imports, DoctorSeverity.Error,
                    $"{rel} imports {Rel(root, missingImport)}, which does not exist.", "The import is unconditional (or its condition is true), so MSBuild stops at it.",
                    rel, "Restore the missing file from the repository, or correct the import path."));
            foreach (var (raw, expanded) in f.RawProjectReferences)
            {
                if (expanded.Contains("$(", StringComparison.Ordinal))
                {
                    var names = PropertyNames(expanded);
                    findings.Add(new($"prop:{rel}:{raw}", DoctorCategory.MsBuildProperties, DoctorSeverity.Error,
                        $"Project reference {raw} in {rel} uses {string.Join(", ", names.Select(n => $"$({n})"))}, which could not be evaluated.",
                        DefinitionSites(root, input.Project, names), rel,
                        "Make the property available to the build (import the file that defines it), or pass it as a build property.",
                        Technical: $"Include=\"{raw}\" → {expanded}"));
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(dir, expanded));
                facts.Add($"  reference {raw} → {Rel(root, target)}");
                if (!IsWithin(root, target)) { outside.Add((rel, raw, target, "project")); continue; }
                if (!File.Exists(target)) { findings.Add(MissingReference(root, rel, raw, expanded, target)); continue; }
                queue.Enqueue(target);
            }
            foreach (var (type, raw, expanded) in f.FileItems)
            {
                var target = Path.GetFullPath(Path.Combine(dir, expanded));
                if (!IsWithin(root, target)) { outside.Add((rel, raw, target, type)); continue; }
                if (File.Exists(target) || Directory.Exists(target)) continue;
                findings.Add(new($"file:{rel}:{raw}", DoctorCategory.SourceFiles, type == "Compile" ? DoctorSeverity.Error : DoctorSeverity.Warning,
                    $"{type} item {Rel(root, target)} (in {rel}) does not exist.", "The file is listed explicitly but is not in the imported source.",
                    rel, "Restore the file from the repository; HCG never creates placeholder source files.", Technical: $"<{type} Include=\"{raw}\" />"));
            }
        }
        if (outside.Count > 0) findings.AddRange(OutsideFindings(input, root, outside));
        foreach (var name in environment)
            findings.Add(new($"env:{name}", DoctorCategory.BuildInput, DoctorSeverity.Info,
                $"Optional build input {name} comes from an environment variable.", "MSBuild reads properties of that name from the environment. HCG does not read or show its value; in Maker's builds it is unset unless you configure it.",
                null, "Nothing to do unless the app needs it. Values that are secrets belong in the build environment, never in project files."));
    }

    private static DoctorFinding MissingReference(string root, string fromRel, string raw, string expanded, string target)
    {
        var name = Path.GetFileName(target);
        var candidates = FindFiles(root, name).ToList();
        var targetRel = Rel(root, target);
        if (candidates.Count == 1 && !raw.Contains("$(", StringComparison.Ordinal))
        {
            var fromDir = Path.GetDirectoryName(Path.Combine(root, fromRel))!;
            var fixedRel = Path.GetRelativePath(fromDir, candidates[0]);
            var newRaw = raw.Contains('\\') ? fixedRel.Replace('/', '\\') : fixedRel.Replace('\\', '/');
            var before = File.ReadAllText(Path.Combine(root, fromRel));
            var after = before.Replace($"Include=\"{raw}\"", $"Include=\"{newRaw}\"", StringComparison.Ordinal);
            return new($"ref:{fromRel}:{raw}", DoctorCategory.ProjectReferences, DoctorSeverity.Error,
                $"{fromRel} references {targetRel}, which does not exist.",
                $"The relative path is broken: the only {name} in the workspace is {Rel(root, candidates[0])}.", fromRel,
                $"Point the reference at {Rel(root, candidates[0])}.",
                after == before ? null : new DoctorRepair($"fix-ref:{fromRel}:{raw}", RepairKind.EditFiles, RepairMode.RequiresApproval,
                    $"Reconnect {name} in {Path.GetFileName(fromRel)}",
                    $"Changes Include=\"{raw}\" to Include=\"{newRaw}\" in {fromRel}. A backup is kept; the change can be rolled back.",
                    Changes: [new DoctorFileChange(fromRel, before, after)]),
                $"Include=\"{raw}\" → {expanded}");
        }
        return new($"ref:{fromRel}:{raw}", DoctorCategory.ProjectReferences, DoctorSeverity.Error,
            $"{fromRel} references {targetRel}, which does not exist.",
            candidates.Count == 0 ? $"No {name} exists anywhere in the imported source: the project is missing, not misplaced."
                : $"{candidates.Count} files named {name} exist ({string.Join(", ", candidates.Take(3).Select(c => Rel(root, c)))}); the right one is not unambiguous.",
            fromRel,
            candidates.Count == 0 ? "Import the repository that contains the project. HCG never creates an empty replacement project."
                : "Choose the intended project and correct the reference by hand.",
            Technical: $"Include=\"{raw}\" → {expanded}");
    }

    /// <summary>
    /// Everything the app needs that lies outside the imported folder. When the import came from a folder and the
    /// targets exist next to it in the original location, the repair is to import the repository root that contains
    /// both, so every relative path resolves as it does in the repository.
    /// </summary>
    private static IEnumerable<DoctorFinding> OutsideFindings(DoctorInput input, string root, List<(string From, string Raw, string Target, string Kind)> outside)
    {
        var repoRoot = RepositoryRoot(input.Project, root, outside.Select(o => o.Target).ToList());
        var importedName = input.Project.SourceKind == ProjectSourceKind.Directory ? Path.GetFileName(Path.TrimEndingDirectorySeparator(input.Project.SourceLocation)) : "the imported folder";
        DoctorRepair repair = repoRoot is not null
            ? new DoctorRepair("import-repository", RepairKind.ImportRepository, RepairMode.RequiresApproval,
                $"Import the repository folder {Path.GetFileName(repoRoot)}",
                $"Imports {repoRoot} as a new project, so {importedName} and the shared code it references keep their original places and every " +
                $"relative path resolves as it does in the repository. Nothing in the original folder is changed, and the current project stays available. " +
                $"Afterwards Build Doctor runs again on the new import.", Target: repoRoot)
            : new DoctorRepair("import-repository", RepairKind.Instructions, RepairMode.Manual,
                "Import the repository root",
                input.Project.SourceKind == ProjectSourceKind.Directory
                    ? $"The shared code was not found next to {input.Project.SourceLocation}. Import the repository root (folder or ZIP) that contains both {importedName} and the shared projects."
                    : "This import came from a ZIP of one folder. Import a ZIP or folder of the whole repository, so the shared projects are included.");
        foreach (var (from, raw, target, kind) in outside)
        {
            var relOutside = Rel(root, target);
            yield return new DoctorFinding($"outside:{from}:{raw}", DoctorCategory.ImportScope, kind is "project" or "Compile" ? DoctorSeverity.Error : DoctorSeverity.Warning,
                $"{Path.GetFileName(target)} ({(kind == "project" ? "project reference" : kind)} in {from}) is outside the imported folder.",
                $"The app is part of a larger repository: {raw} resolves to {relOutside}. Only {importedName} was imported, so the folder layout the project expects is not in the workspace." +
                (repoRoot is not null ? $" The file exists in the original repository ({Path.Combine(repoRoot, Path.GetRelativePath(repoRoot, target))})." : ""),
                from, repair.Mode == RepairMode.RequiresApproval ? "Import the repository folder (keeps the layout)." : repair.Description, repair, Technical: $"{raw} → {target}");
        }
    }

    /// <summary>The smallest original folder that contains the import and every outside target, widened to the repository root (.git or hcg-apps.json) when there is one.</summary>
    private static string? RepositoryRoot(ProjectRecord project, string root, IReadOnlyList<string> outsideTargets)
    {
        if (project.SourceKind != ProjectSourceKind.Directory || !Directory.Exists(project.SourceLocation)) return null;
        var original = Path.GetFullPath(project.SourceLocation);
        var mapped = outsideTargets.Select(t => Path.GetFullPath(Path.Combine(original, Path.GetRelativePath(root, t)))).ToList();
        if (mapped.Any(t => !File.Exists(t) && !Directory.Exists(t))) return null;
        var common = mapped.Aggregate(original, CommonAncestor);
        for (var dir = common; dir is not null && Path.GetPathRoot(dir) != dir; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "hcg-apps.json"))) return dir;
        return common;
    }

    private static string CommonAncestor(string a, string b)
    {
        var x = Path.GetFullPath(a).Split(Path.DirectorySeparatorChar);
        var y = Path.GetFullPath(Path.GetDirectoryName(b) ?? b).Split(Path.DirectorySeparatorChar);
        var n = 0;
        while (n < x.Length && n < y.Length && x[n] == y[n]) n++;
        var joined = string.Join(Path.DirectorySeparatorChar, x.Take(n));
        return joined.Length == 0 ? Path.DirectorySeparatorChar.ToString() : joined;
    }

    /// <summary>Where each property is defined, in the import and (for folder imports) in the original repository.</summary>
    private static string DefinitionSites(string root, ProjectRecord project, IReadOnlyList<string> names)
    {
        var parts = new List<string>();
        foreach (var name in names)
        {
            if (MsBuildProjectFile.LooksLikeEnvironmentVariable(name)) { parts.Add($"$({name}) is read from the environment variable {name}, which is not set for this build."); continue; }
            var sites = MsBuildFiles(root).Where(f => DefinesProperty(f, name)).Select(f => Rel(root, f)).Take(3).ToList();
            parts.Add(sites.Count > 0
                ? $"$({name}) is defined in {string.Join(", ", sites)}, but its value could not be evaluated here (a condition or property function HCG cannot evaluate statically; Evaluate with MSBuild gives the exact value)."
                : $"$({name}) is not defined in any project or props file in the import.");
        }
        return string.Join(" ", parts);
    }

    private static bool DefinesProperty(string file, string name)
    {
        try { return Regex.IsMatch(File.ReadAllText(file), $"<{Regex.Escape(name)}[\\s>]"); }
        catch (IOException) { return false; }
    }

    // ───────────── .NET for iOS specifics ─────────────

    private static void DotNetIosChecks(DoctorInput input, string root, DotNetIosProjectInfo dn, List<DoctorFinding> findings, List<string> facts)
    {
        var head = Path.Combine(root, dn.ProjectPath);
        var headDir = Path.GetDirectoryName(head)!;
        var f = DotNetProjectInspector.LoadProject(root, head);
        var r = input.Report;
        var output = input.Output;
        var simulator = output == MobileOutput.IosSimulatorApp;
        facts.Add($"App {dn.ApplicationId ?? "(no ApplicationId)"} · {dn.TargetFramework} · iOS {dn.MinimumOS ?? "?"}+ · workload pin {dn.WorkloadPin ?? "none"} · Xcode {dn.RequiredXcode ?? "not pinned"}");

        // Bundle identifier.
        if (dn.ApplicationId is null)
            findings.Add(new("bundle:none", DoctorCategory.BundleIdentifier, DoctorSeverity.Warning, "The app has no ApplicationId (bundle identifier) that HCG could read.",
                "ApplicationId is missing or built from properties that are not available in this import.", dn.ProjectPath, "Set ApplicationId in the project (reverse-DNS, e.g. com.company.app)."));
        else if (!BundleIdRegex().IsMatch(dn.ApplicationId))
            findings.Add(new("bundle:invalid", DoctorCategory.BundleIdentifier, DoctorSeverity.Error, $"Bundle identifier {dn.ApplicationId} is not valid.",
                "Apple allows letters, digits, hyphens and dots, with at least two segments.", dn.ProjectPath, "Correct ApplicationId in the project."));

        // Entitlements file.
        if (dn.Entitlements is { } ent && !File.Exists(Path.Combine(headDir, ent)))
            findings.Add(new("entitlements:missing", DoctorCategory.Signing, DoctorSeverity.Error, $"The entitlements file {ent} does not exist.",
                "CodesignEntitlements names a file that is not in the project folder.", dn.ProjectPath, "Restore the file, or correct CodesignEntitlements."));

        // Target framework vs installed SDKs.
        var sdkRow = r?.Find(ToolchainIds.DotNetSdk);
        var sdks = sdkRow is { IsAvailable: true } ? new[] { sdkRow.Version }.Concat(sdkRow.AdditionalVersions).OfType<string>().ToList() : [];
        if (sdks.Count > 0) facts.Add(".NET SDKs installed: " + string.Join(", ", sdks));
        var tfmMajor = TfmMajor(dn.TargetFramework);
        if (sdks.Count > 0 && tfmMajor > 0 && !sdks.Any(v => int.TryParse(v.Split('.')[0], out var maj) && maj >= tfmMajor))
            findings.Add(new("tfm:sdk", DoctorCategory.TargetFramework, DoctorSeverity.Error, $"{dn.TargetFramework} needs .NET SDK {tfmMajor} or newer; installed: {string.Join(", ", sdks)}.",
                "The target framework is newer than every installed SDK.", dn.ProjectPath, $"Install .NET SDK {tfmMajor}.0 (https://dotnet.microsoft.com/download). HCG never installs SDKs."));

        // global.json.
        if (NearestGlobalJson(root, headDir) is { } gjPath && GlobalJson.TryRead(gjPath) is { SdkVersion: { } want } gj && sdks.Count > 0 && !gj.IsSatisfiedBy(sdks))
            findings.Add(new("sdk:globaljson", DoctorCategory.Sdk, DoctorSeverity.Error,
                $"{Rel(root, gjPath)} asks for .NET SDK {want} (rollForward {gj.RollForward ?? "patch"}); no installed SDK satisfies it ({string.Join(", ", sdks)}).",
                "The SDK resolver will refuse to build until a matching SDK is installed.", Rel(root, gjPath), $"Install .NET SDK {want} or a later version that the rollForward policy allows."));

        // Xcode the project is built with vs the selected one.
        var xcode = r?.Xcode?.VerifiedVersion;
        if (dn.RequiredXcode is { } wantXcode && xcode is not null && !SameMinor(wantXcode, xcode))
            findings.Add(new("xcode:pin", DoctorCategory.Xcode, simulator ? DoctorSeverity.Warning : DoctorSeverity.Error,
                $"The project is built with Xcode {wantXcode} ({dn.WorkloadPinFile}); the selected Xcode is {xcode}.",
                $"Each .NET iOS workload version requires a specific Xcode{(dn.WorkloadPin is { } p ? $"; the pinned workload {p} expects Xcode {wantXcode}" : "")}. Installing the workload alone does not change that.",
                dn.WorkloadPinFile, $"Install Xcode {wantXcode} and select it in Toolchains (HCG uses it for this project only), or update the project's pins together.",
                new DoctorRepair("select-xcode", RepairKind.Instructions, RepairMode.Manual, $"Select Xcode {wantXcode}", $"Toolchains › Xcode: choose Xcode {wantXcode}. HCG never installs Xcode.")));

        // Runtime identifier.
        if (f?.Get("RuntimeIdentifier") is { } rid && !rid.Contains("$(", StringComparison.Ordinal))
        {
            var isSim = rid.StartsWith("iossimulator-", StringComparison.OrdinalIgnoreCase);
            if (simulator != isSim)
                findings.Add(new("rid:mismatch", DoctorCategory.RuntimeIdentifier, DoctorSeverity.Error,
                    $"The project fixes RuntimeIdentifier={rid}, which does not fit a {(simulator ? "Simulator" : "device")} build.",
                    "A fixed RuntimeIdentifier overrides the one the build output needs.", dn.ProjectPath, "Remove the fixed RuntimeIdentifier or make it conditional on the build."));
            else if (isSim && rid.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
                findings.Add(new("rid:x64sim", DoctorCategory.Architecture, DoctorSeverity.Warning, $"RuntimeIdentifier={rid} builds for Intel Simulators on an Apple silicon Mac.",
                    "x64 Simulator apps need Rosetta Simulator runtimes.", dn.ProjectPath, "Use iossimulator-arm64 on Apple silicon."));
        }

        // Configurations.
        if (f?.Get("Configurations") is { } configs && !configs.Contains("$(", StringComparison.Ordinal))
        {
            var needed = simulator ? "Debug" : "Release";
            if (!configs.Split(';').Any(c => c.Trim().Equals(needed, StringComparison.OrdinalIgnoreCase)))
                findings.Add(new("config:missing", DoctorCategory.Configuration, DoctorSeverity.Error, $"The project's Configurations ({configs}) do not include {needed}.",
                    $"HCG builds {(simulator ? "Simulator apps in Debug" : "archives and IPAs in Release")}.", dn.ProjectPath, $"Add {needed} to Configurations."));
        }

        // Packages restored?
        var assets = Path.Combine(headDir, "obj", "project.assets.json");
        if (!File.Exists(assets))
            findings.Add(new("restore:none", DoctorCategory.Packages, DoctorSeverity.Info, "NuGet packages have not been restored in this workspace yet.",
                "The build restores them first; restoring now surfaces missing or unreachable packages before a long build.", dn.ProjectPath,
                "Restore packages (Build Doctor runs dotnet restore after your approval; it runs the project's restore targets).",
                new DoctorRepair("restore", RepairKind.RestorePackages, RepairMode.RequiresApproval, "Restore NuGet packages",
                    $"Runs dotnet restore for {dn.ProjectPath} in the workspace (project code: MSBuild restore targets). Packages come from the project's NuGet.config sources.",
                    Command: $"dotnet restore \"{dn.ProjectPath}\"")));
    }

    // ───────────── Toolchain readiness (reused from Mobile Build Studio) ─────────────

    private static void Readiness(DoctorInput input, List<DoctorFinding> findings, List<string> facts)
    {
        var readiness = MobileReadiness.Evaluate(input.Mobile, input.Output, input.Options, input.Report);
        var otherHost = readiness.Items.Where(i => i.Required && i.State == ReadinessState.OtherHost).ToList();
        if (otherHost.Count > 0)
        {
            findings.Add(new("host:mac", DoctorCategory.Host, DoctorSeverity.Error, "This computer is not a Mac: iOS apps are built on macOS with Xcode.",
                "Apple's iOS SDK, linker and codesign run only on macOS. Nothing on this computer needs fixing.", null,
                "Open the project in HCG Executable Maker on a Mac with Xcode, or use the macOS CI runner. Project checks below still apply.",
                Technical: string.Join("\n", otherHost.Select(i => $"{i.Tool}: {i.Detail}"))));
        }
        foreach (var item in readiness.Items)
        {
            facts.Add($"Readiness {item.Tool}: {item.State} — {item.Detail}");
            if (!item.Required || item.State is ReadinessState.Ready or ReadinessState.NotNeeded or ReadinessState.NotVerifiable or ReadinessState.OtherHost) continue;
            if (item.Tool == "Project references") continue; // the project graph above reports each reference precisely
            var category = item.Tool switch
            {
                ".NET SDK" => DoctorCategory.Sdk,
                var t when t.Contains("workload", StringComparison.OrdinalIgnoreCase) => DoctorCategory.Workload,
                var t when t.Contains("Xcode", StringComparison.Ordinal) || t.Contains("iOS SDK", StringComparison.Ordinal) => DoctorCategory.Xcode,
                var t when t.Contains("rovision", StringComparison.Ordinal) => DoctorCategory.Provisioning,
                var t when t.Contains("ign", StringComparison.Ordinal) || t.Contains("ertificate", StringComparison.Ordinal) => DoctorCategory.Signing,
                var t when t.Contains("eployment", StringComparison.Ordinal) => DoctorCategory.Configuration,
                _ => DoctorCategory.Configuration,
            };
            DoctorRepair? repair = category switch
            {
                DoctorCategory.Workload => new DoctorRepair("install-workload", RepairKind.InstallWorkload, RepairMode.RequiresApproval,
                    "Install the .NET iOS workload", "Runs the command below after you approve it, then checks that the workload is registered and matches the project. " +
                    "It may need administrator rights; HCG never installs anything without approval.",
                    Command: input.Mobile.DotNetIos?.WorkloadInstallCommand ?? "dotnet workload install ios"),
                DoctorCategory.Signing or DoctorCategory.Provisioning => new DoctorRepair("configure-signing", RepairKind.ConfigureSigning, RepairMode.Manual,
                    "Configure signing", "Signing & Security lists the certificates and profiles in your keychain; choose one, or build for the Simulator, which needs no signing."),
                _ => item.Fix is { } fix ? new DoctorRepair($"fix:{item.Tool}", RepairKind.Instructions, RepairMode.Manual, item.Tool, fix) : null,
            };
            findings.Add(new($"ready:{item.Tool}", category, DoctorSeverity.Error, $"{item.Tool}: {item.Detail}",
                item.State switch
                {
                    ReadinessState.NotFound => "Not installed or not found.",
                    ReadinessState.IncompatibleVersion => "Installed, but the version does not fit this project.",
                    ReadinessState.InvalidPath => "The configured path is not valid.",
                    _ => "Required for this output.",
                },
                null, item.Fix ?? "See Toolchains.", repair, Technical: item.Path is null ? null : $"{item.Path} {item.Version}"));
        }
    }

    // ───────────── Helpers ─────────────

    private static IReadOnlyList<string> PropertyNames(string s) => PropertyRef().Matches(s).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<string> FindFiles(string root, string name) =>
        SafeEnumerate(root, name).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or ".git" or "node_modules"));

    private static IEnumerable<string> MsBuildFiles(string root) =>
        new[] { "*.props", "*.targets", "*.csproj", "*.fsproj" }.SelectMany(p => SafeEnumerate(root, p))
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or ".git" or "node_modules"));

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12 }).ToList(); }
        catch (IOException) { return []; }
    }

    private static string? NearestGlobalJson(string root, string dir)
    {
        for (var d = dir; d is not null && IsWithin(root, d) || d == root; d = Path.GetDirectoryName(d))
        {
            var p = Path.Combine(d!, "global.json");
            if (File.Exists(p)) return p;
            if (d == root) break;
        }
        return null;
    }

    private static int TfmMajor(string tfm) => TfmRegex().Match(tfm) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;

    private static bool SameMinor(string a, string b)
    {
        static string MajorMinor(string v) => string.Join('.', v.Split('.').Take(2));
        return MajorMinor(a) == MajorMinor(b);
    }

    internal static bool IsWithin(string root, string path) =>
        path == root || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    [GeneratedRegex(@"\$\(([A-Za-z_][\w.]*)\)")]
    private static partial Regex PropertyRef();

    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex BundleIdRegex();

    [GeneratedRegex(@"^net(\d+)\.")]
    private static partial Regex TfmRegex();
}
