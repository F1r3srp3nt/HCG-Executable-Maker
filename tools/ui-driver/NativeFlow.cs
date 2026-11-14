using System.Text.Json;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// The C/C++ path through the real New Build wizard: Inspect (states, trusted CMake configure, target choice) →
/// Configure → Package → Build → Results. Options: --project dir/CMakeLists.txt|dir/Makefile|dir, --target NAME,
/// --make-target T --expected-output PATH, --expect-targets a,b --expect-libraries x,y, --expect-unsupported. A folder with
/// an app project next to a Theos project: --build-route theos-deb|xcode-ipa (default theos-deb).
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunNativeAsync(BuildWizardViewModel wizard, HostEnvironment host, string output)
    {
        // A folder with an app project next to a Theos project (HCG Nexus with HCGNexus.xcodeproj): Build route chooses
        // which one New Build builds. --build-route theos-deb|xcode-ipa (default theos-deb on such a folder).
        if (wizard.HasBuildRoutes || Arg("build-route") is not null)
        {
            foreach (var r in wizard.BuildRouteOptions) Log($"Build route offered: {r.Label} — {r.Description}");
            Check(wizard.HasBuildRoutes, "Build route choice shown: " + string.Join(" | ", wizard.BuildRouteOptions.Select(r => r.Label)));
            var lead = wizard.SelectedBuildRoute;
            if (lead?.Id == HCG.ExecutableMaker.ProjectInspection.BuildRoutes.XcodeIpa)
                Check(wizard.IsMobileProject && !wizard.IsNativeProject && !wizard.CanConfigure, "iOS app (IPA, Xcode) route: built in Mobile Build Studio, no .deb offered in New Build");
            Shot("native-route-" + lead?.Id);
            var want = Arg("build-route", HCG.ExecutableMaker.ProjectInspection.BuildRoutes.TheosDeb);
            var route = wizard.BuildRouteOptions.FirstOrDefault(r => r.Id == want || r.Label == want);
            Check(route is not null, $"Build route {want} offered");
            if (route is null) return 1;
            wizard.SelectedBuildRoute = route;
            Log($"Build route chosen: {route.Label}");
            if (route.Id == HCG.ExecutableMaker.ProjectInspection.BuildRoutes.TheosDeb)
                Check(!wizard.IsMobileProject && wizard.Analysis!.RecommendedFormats.SequenceEqual([PackagingFormat.JailbreakDeb]), "Jailbreak package (.deb, Theos) route: only a .deb is offered, never an IPA");
            else return Failures.Count == 0 ? 0 : 1;
        }
        var a = wizard.Analysis!;
        Log($"Inspection: {a.InspectorId} · {a.Framework} · {a.ApplicationType} · {a.Support} — {a.SupportSummary}");
        foreach (var d in a.Diagnostics) Log($"  {d.Severity}: {d.Message}{(d.Path is null ? "" : " (" + d.Path + ")")}");
        Check(wizard.IsNativeProject, "Recognized by the C/C++ inspector");
        Check(!a.SupportSummary.Contains("planned", StringComparison.OrdinalIgnoreCase), "No 'support planned' placeholder in the inspection result");
        foreach (var o in wizard.ProjectOptions) Log($"Offered: {o.Path} — {o.Project.ApplicationType}");
        if (wizard.ProjectOptions.Count > 1) Check(wizard.SelectedProject is null, "Several C/C++ projects: none selected automatically");

        var wanted = Arg("project");
        var choice = wanted is null ? wizard.ProjectOptions.FirstOrDefault() : wizard.ProjectOptions.FirstOrDefault(p => p.Path == wanted);
        Check(choice is not null, $"Project {wanted ?? "(only one)"} offered");
        if (choice is null) { Shot("native-inspect"); return 1; }
        wizard.SelectedProject = choice;
        LogNative(wizard);
        Shot("native-inspect");

        if (Arg("expect-native-state") is { } expectedNative)
        {
            if (Arg("theos-scheme") is { } ts && wizard.TheosSchemes.FirstOrDefault(x => x.Scheme.SelectorValue == ts || x.Scheme.PackageScheme == ts) is { } tsi) wizard.SelectedTheosScheme = tsi;
            Check(wizard.NativeTag.Text == expectedNative, $"State: [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
            foreach (var g in wizard.NativeToolchainGaps) Log("  Missing: " + g);
            Check(!wizard.CanConfigure && !wizard.NextCommand.CanExecute(null), "Cannot continue to Configure in this state");
            return Failures.Count == 0 ? 0 : 1;
        }
        if (_args.ContainsKey("expect-unsupported"))
        {
            Check(wizard.NativeTag.Text is "Build adapter unavailable" or "Project implementation incomplete", $"State: {wizard.NativeTag.Text}");
            Check(!wizard.CanConfigure && !wizard.NextCommand.CanExecute(null), "Cannot continue to Configure");
            Check(wizard.NativeStatus.Length > 0, "Explains why: " + wizard.NativeStatus);
            return Failures.Count == 0 ? 0 : 1;
        }

        if (wizard.ShowNativeTheos)
        {
            Log("Theos products:\n" + wizard.TheosProducts);
            Log("Theos package: " + wizard.TheosPackage.Replace("\n", " | "));
            foreach (var sc in wizard.TheosSchemes) Log("Configuration offered: " + sc.Name);
            var wantScheme = Arg("theos-scheme");
            var item = wantScheme is null ? wizard.SelectedTheosScheme : wizard.TheosSchemes.FirstOrDefault(x => x.Scheme.SelectorValue == wantScheme || x.Scheme.PackageScheme == wantScheme);
            Check(item is not null, $"Configuration {wantScheme ?? "(default)"} offered");
            if (item is null) return 1;
            wizard.SelectedTheosScheme = item;
            Log("Configuration: " + wizard.TheosSchemeDetail.Replace("\n", " | "));
            if (Arg("expect-refused") is { } refused)
            {
                wizard.SelectedTheosScheme = wizard.TheosSchemes.First(x => x.Scheme.SelectorValue == refused);
                Check(wizard.NativeTag.Text == "Configuration required" && !wizard.CanConfigure, $"{refused} is refused by the project: [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
                wizard.SelectedTheosScheme = item;
            }
        }
        else if (wizard.ShowNativeCMake)
        {
            Check(wizard.NativeTag.Text == "Configuration required", $"Before configuring: [{wizard.NativeTag.Text}]");
            Check(!wizard.CanConfigure, "Cannot continue before the project is configured");
            Check(!wizard.ConfigureNativeCommand.CanExecute(null), "Configure refused before trust is confirmed");
            wizard.NativeConfigureTrusted = true;
            Check(wizard.ConfigureNativeCommand.CanExecute(null), "Configure enabled after trust is confirmed");
            await wizard.ConfigureNativeCommand.ExecuteAsync(null);
            await WaitUntil(() => !wizard.IsConfiguringNative, TimeSpan.FromMinutes(11), "CMake configuration");
            foreach (var l in wizard.NativeConfigureLog.Take(60)) Log("  configure | " + l);
            Check(wizard.NativeConfigureError is null, "CMake configured" + (wizard.NativeConfigureError is { } ce ? ": " + ce : ""));
            Log("Configured: " + wizard.NativeConfiguredSummary.Replace("\n", " | "));
            Log(wizard.NativeLibrariesText);
            var targets = wizard.ProjectOptions.Where(o => o.Path.Contains('#')).Select(o => o.Path[(o.Path.IndexOf('#') + 1)..]).ToList();
            Log("Executable targets under Project to build: " + string.Join(", ", targets));
            ExpectSet("expect-targets", targets, "Executable targets");
            if (Arg("expect-libraries") is { } libs)
                foreach (var lib in libs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    Check(wizard.NativeLibrariesText.Contains(lib, StringComparison.Ordinal), $"Library {lib} listed separately");
                    Check(!targets.Contains(lib), $"Library {lib} is not offered as a runnable target");
                }
            if (targets.Count > 1) Check(wizard.SelectedProject is null, "Several executables: none selected automatically");
            Shot("native-configured");
            var target = Arg("target") ?? (targets.Count == 1 ? targets[0] : null);
            var entry = wizard.ProjectOptions.FirstOrDefault(o => o.Path.EndsWith("#" + target, StringComparison.Ordinal));
            Check(entry is not null, $"Target {target} offered");
            if (entry is null) return 1;
            wizard.SelectedProject = entry;
            if (Arg("runtime-files") is { } rf) wizard.NativeRuntimeFilesText = rf.Replace(',', '\n');
        }
        else if (wizard.ShowNativeMake)
        {
            Log("Make targets read from the Makefile: " + string.Join(", ", wizard.NativeMakeTargets));
            Check(wizard.NativeTag.Text == "Configuration required", $"Before naming the output: [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
            Check(!wizard.CanConfigure, "Cannot continue before the expected output is named");
            wizard.NativeMakeTarget = Arg("make-target", "all");
            wizard.NativeExpectedOutput = Arg("expected-output", "");
            if (Arg("make-vars") is { } mv) wizard.NativeMakeVariablesText = mv.Replace(',', '\n');
        }
        else if (wizard.ShowNativeSources)
        {
            foreach (var s in wizard.NativeSourceItems) Log($"Source {s.Path} ({s.Detail}) selected: {s.IsSelected}");
            if (Arg("sources") is { } src)
                foreach (var s in wizard.NativeSourceItems) s.IsSelected = src.Split(',').Contains(s.Path);
            Log("Generated build configuration:\n" + wizard.NativeGeneratedPreview);
        }
        LogNative(wizard);
        Check(wizard.NativeTag.Text == "Ready to build" && wizard.CanConfigure, $"State: [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
        Shot("native-ready");
        ShotScrolledToEnd("native-ready-details");
        if (!wizard.CanConfigure) return 1;

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsConfigureStep && wizard.IsNativeBuild && !wizard.IsDotNetBuild, "Configure step for a C/C++ build");
        if (_args.ContainsKey("expect-app-bundle")) Check(wizard.IsNativeMacBundle, "The target is a macOS app bundle (MACOSX_BUNDLE)");
        foreach (var p in wizard.PlatformOptions) Log($"Platform {p.Name}: {(p.IsEnabled ? "available" : "not available — " + p.Note)}");
        foreach (var o in wizard.ArchitectureOptions) Log($"Architecture option: {o.Name}");
        if (wizard.IsTheosBuild)
            Check(wizard.PlatformOptions.Where(p => p.IsEnabled).Select(p => p.Value).SequenceEqual([TargetPlatform.IOS]), "Theos: the only target is iOS (jailbroken, .deb)");
        else
            Check(wizard.PlatformOptions.Where(p => p.IsEnabled).Select(p => p.Value).SequenceEqual([host.Platform]), "Only this computer's platform can be chosen");
        if (Arg("arch") is { } archArg)
        {
            var arch = archArg switch { "universal" => TargetArchitecture.Universal, "x64" or "x86_64" => TargetArchitecture.X64, _ => TargetArchitecture.Arm64 };
            var opt = wizard.ArchitectureOptions.FirstOrDefault(o => o.Value == arch);
            Check(opt is not null, $"Architecture {archArg} offered on this host");
            if (opt is not null) wizard.SelectedArchitecture = opt;
        }
        Log($"Target: {wizard.SelectedPlatform?.Name} {wizard.SelectedArchitecture?.Name} · {wizard.PlatformNote}");
        Log($"Identity: {wizard.AppName} {wizard.Version} ({wizard.AppId})");
        if (Arg("expect-version") is { } ev) Check(wizard.Version == ev, $"Version {wizard.Version} (expected {ev}, from project())");
        wizard.OutputDirectory = Path.Combine(output, "artifacts");
        foreach (var err in wizard.ConfigErrors) Log("Config error: " + err);
        Check(wizard.ConfigErrors.Count == 0, "Configuration valid");
        Shot("native-configure");
        if (wizard.ConfigErrors.Count > 0) return 1;

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsPackageStep, "Package step");
        foreach (var o in wizard.PackagingOptions) Log($"Format {o.Name}: {o.Status.Text}{(o.HasReason ? " — " + o.Reason : "")}");
        if (wizard.IsTheosBuild)
            Check(wizard.PackagingOptions.Where(o => o.IsEnabled).Select(o => o.Option.Format).SequenceEqual([PackagingFormat.JailbreakDeb]), "Theos: only the .deb is offered (never an IPA or archive)");
        var formats = Arg("formats", wizard.IsTheosBuild ? "deb" : "folder,zip").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f switch
            {
                "app" => PackagingFormat.MacAppBundle, "zip" => PackagingFormat.PortableZip, "folder" => PackagingFormat.PortableFolder, "deb" => PackagingFormat.JailbreakDeb,
                _ => throw new ArgumentException("Unknown format " + f),
            }).ToHashSet();
        foreach (var o in wizard.PackagingOptions) o.IsSelected = o.IsEnabled && formats.Contains(o.Option.Format);
        foreach (var f in formats)
            Check(wizard.PackagingOptions.Any(o => o.Option.Format == f && o.IsSelected), $"Format {f.DisplayName()} available and selected");
        Check(wizard.PackagingOptions.Where(o => o.Option.Availability != PackagingAvailability.Available).All(o => !o.IsSelected), "Unavailable formats cannot be selected");
        Shot("native-package");

        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsBuildStep, "Build step");
        foreach (var row in wizard.Summary) Log($"Summary {row.Label}: {row.Value}");
        Check(!wizard.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        wizard.TrustAcknowledged = true;
        var buildTask = wizard.StartBuildCommand.ExecuteAsync(null);
        var sawBuilding = false;
        while (!buildTask.IsCompleted)
        {
            if (!sawBuilding && wizard.IsBuilding && wizard.NativeTag.Text == "Building") { sawBuilding = true; Shot("native-building"); }
            await Task.Delay(100);
        }
        await buildTask;
        var stages = wizard.StageHistory.ToList();
        Log("Stages: " + string.Join(" → ", stages));
        Check(sawBuilding, "State shown while building: Building");
        var result = wizard.Result;
        Check(result?.Status == BuildStatus.Succeeded, $"Build {result?.Status}: {wizard.ResultDetail}");
        Check(wizard.NativeTag.Text == "Artifact verified", $"State after the build: [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
        Shot("native-results");
        if (result is null) return 1;
        foreach (var (k, v) in result.ToolchainVersions) Log($"Tool {k}: {v}");
        foreach (var m in result.Messages.Take(30)) Log($"Message {m.Severity}: {m.Text}");
        foreach (var art in result.Artifacts)
        {
            Log($"Artifact {art.Format.DisplayName()}: {art.Path} · {art.SizeBytes:N0} bytes · sha256 {art.Sha256} · {art.Verification}");
            foreach (var c in art.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
            if (art.Verification == VerificationStatus.Failed) Failures.Add($"Artifact {art.Path} failed verification");
        }
        // For independent checks by the caller: the artifact folder (or .app) and the ZIP.
        File.WriteAllText(Path.Combine(output, $"{_prefix}-artifacts.txt"),
            (result.Artifacts.FirstOrDefault(x => x.Format is PackagingFormat.MacAppBundle or PackagingFormat.PortableFolder or PackagingFormat.JailbreakDeb)?.Path ?? "") + "\n" +
            (result.Artifacts.FirstOrDefault(x => x.Format == PackagingFormat.PortableZip)?.Path ?? "") + "\n");
        File.WriteAllText(Path.Combine(output, $"{_prefix}-build.json"), JsonSerializer.Serialize(new
        {
            result.Status, result.FailureReason, result.ToolchainVersions, Settings = result.Settings, Stages = stages,
            Artifacts = result.Artifacts.Select(x => new { x.Format, x.Path, x.Sha256, x.SizeBytes, x.Verification, x.Checks }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == BuildStatus.Succeeded && Failures.Count == 0 ? 0 : 1;
    }

    private static void LogNative(BuildWizardViewModel wizard)
    {
        Log($"C/C++ panel: {wizard.NativeKindLabel} [{wizard.NativeTag.Text}] {wizard.NativeStatus}");
        foreach (var line in wizard.NativeFacts.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log("  " + line);
        foreach (var g in wizard.NativeToolchainGaps) Log("  Missing tool: " + g);
        foreach (var n in wizard.NativeNotes) Log("  Note: " + n);
        if (wizard.NativeConfigurationProblem.Length > 0) Log("  Configuration required: " + wizard.NativeConfigurationProblem);
    }
}
