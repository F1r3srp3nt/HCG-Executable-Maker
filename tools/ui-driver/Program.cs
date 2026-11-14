// Drives HCG Executable Maker's real New Build wizard headlessly (Avalonia headless platform + Skia rendering).
//
// The app's own App, MainWindow, BuildWizardView and BuildWizardViewModel are used unchanged; the driver presses the
// same commands the buttons are bound to and sets the same properties the inputs are bound to. Only the OS file
// picker (it returns --source) and modal dialogs (logged; confirmations answered "yes") are scripted.
//
// Usage (Swift package → macOS app):
//   dotnet run --project tools/ui-driver -- --source <folder|zip> --package distil --product TokenLab --name Distil
//       --bundle-id dev.tokenlab.prototype --version 0.4.0 --build-number 4 [--category public.app-category.developer-tools]
//       [--usage NSMicrophoneUsageDescription=text] [--kind gui|cli] [--arch arm64|x64|universal] [--formats app,zip,dmg]
//       [--expect-products TokenLab] [--expect-libraries TokenLabCore] [--expect-tests TokenLabCoreTests]
//       --output <dir> --screenshots <dir> [--prefix distil]
//   --expect-blocked-host: on Windows/Linux, prove the wizard refuses to evaluate/build the package and explains why.
//   Packaging contract (AppPackaging.json): --expect-contract-command "<argv as shown>" proves the command is shown and the
//       build is refused until confirmed; --expect-contract-helpers Contents/Helpers/x[,…]; --confirm-contract ticks
//       “Run these commands while building”.
// Usage (Electron app, built with the project's own electron-builder workflow):
//   dotnet run --project tools/ui-driver -- --electron --source <folder|zip> [--project <dir>] [--arch arm64|x64]
//       [--formats app,zip,dmg | folder] [--expect-name N] [--expect-bundle-id ID] [--expect-version V] --output <dir> --screenshots <dir>
//   --expect-not-desktop: prove a web-only / service project is explained (what is missing) and cannot be configured.
// Usage (browser app → desktop app: preview, create the desktop workspace, then build it as above):
//   dotnet run --project tools/ui-driver -- --prepare-desktop --source <folder with a Vite + React app> [--web-project dir/package.json]
//     [--desktop-name N] [--arch arm64|x64] [--formats app,zip,dmg|folder] [--convert-again]
//       [--desktop-app-id ID] [--formats app,zip,dmg | folder] --output <dir> --screenshots <dir>
//   Distribution (macOS): --formats …,pkg,mas  --select-xcode <Xcode.app> (HCG-only selection via Toolchains › Browse…)
//       --expect-xcode-state Ready  --signing adhoc|devid  --confirm-entitlements  --test-install [launch]
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using AppClass = HCG.ExecutableMaker.App.App;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.App.Views;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.UiDriver;

internal static partial class Program
{
    private static Dictionary<string, string> _args = new();
    private static readonly List<string> Report = [];
    private static readonly List<string> Failures = [];
    private static MainWindow? _window;
    private static string _shots = "";
    private static string _prefix = "swift";
    private static int _shotNumber;

    public static int Main(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
            if (argv[i].StartsWith("--", StringComparison.Ordinal))
                _args[argv[i][2..]] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? argv[++i] : "true";
        if (_args.ContainsKey("account-signed-in") && !_args.ContainsKey("source")) _args["source"] = Path.GetTempPath();
        if (!_args.ContainsKey("source") || (!_args.ContainsKey("package") && !_args.ContainsKey("electron") && !_args.ContainsKey("native") && !_args.ContainsKey("mobile") && !_args.ContainsKey("prepare-desktop") && !_args.ContainsKey("account-pages") && !_args.ContainsKey("account-signed-in") && !_args.ContainsKey("port")))
        {
            Console.Error.WriteLine("Usage: hcg-ui-driver --source <folder|zip> --package <dir> [--product P --name N …] --output <dir> --screenshots <dir>");
            return 2;
        }
        var output = Path.GetFullPath(Arg("output", Path.Combine(Path.GetTempPath(), "hcg-ui-out")));
        _shots = Path.GetFullPath(Arg("screenshots", Path.Combine(output, "screenshots")));
        _prefix = Arg("prefix", "swift");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(_shots);
        // Isolated data folder: imports, history and SwiftPM scratch space do not touch a real installation.
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, Path.Combine(output, "hcg-data"));
        if (_args.ContainsKey("account-signed-in")) PrepareMockHcg(Path.Combine(output, "hcg-data"));

        var source = Path.GetFullPath(_args["source"]);
        AppClass.ServiceOverrides = services =>
        {
            services.AddSingleton<IPlatformShell>(new ScriptedShell(source));
            services.AddSingleton<IDialogService>(new LoggingDialogs());
        };

        AppBuilder.Configure<AppClass>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var exit = 1;
        var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { exit = await RunAsync(output); }
            catch (Exception ex)
            {
                Failures.Add("Driver error: " + ex);
                Shot("error");
            }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);

        File.WriteAllLines(Path.Combine(output, $"{_prefix}-ui-driver-report.txt"), Report.Concat(Failures.Select(f => "FAIL: " + f)));
        foreach (var f in Failures) Console.Error.WriteLine("FAIL: " + f);
        Console.WriteLine(Failures.Count == 0 && exit == 0 ? "UI DRIVER: PASSED" : "UI DRIVER: FAILED");
        return Failures.Count == 0 ? exit : 1;
    }

    private static string Arg(string name, string fallback) => _args.GetValueOrDefault(name, fallback);
    private static string? Arg(string name) => _args.GetValueOrDefault(name);

    private static void Log(string line)
    {
        Report.Add(line);
        Console.WriteLine(line);
    }

    private static void Check(bool ok, string what)
    {
        Log((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) Failures.Add(what);
    }

    private static void Shot(string name)
    {
        if (_window is null) return;
        var path = Path.Combine(_shots, $"{_prefix}-{++_shotNumber:00}-{name}.png");
        _window.CaptureRenderedFrame()?.Save(path);
        Log($"Screenshot: {path}");
    }

    /// <summary>Scrolls the page to its end (the Distribution section sits below the formats), captures, scrolls back.</summary>
    private static void ShotScrolledToEnd(string name)
    {
        if (_window is null) return;
        var scrollers = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_window).OfType<Avalonia.Controls.ScrollViewer>()
            .Where(sv => sv.IsEffectivelyVisible && sv.Extent.Height > sv.Viewport.Height).ToList();
        var saved = scrollers.Select(sv => sv.Offset).ToList();
        foreach (var sv in scrollers) sv.Offset = new Vector(sv.Offset.X, sv.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        Shot(name);
        for (var i = 0; i < scrollers.Count; i++) scrollers[i].Offset = saved[i];
    }

    private static string? _lastOutput;

    /// <summary>A stage budget: --budget-STAGE minutes (e.g. --budget-compilation 12), else the default.</summary>
    private static TimeSpan Budget(string stage, double defaultMinutes) =>
        TimeSpan.FromMinutes(Arg("budget-" + stage) is { } m ? double.Parse(m, System.Globalization.CultureInfo.InvariantCulture) : defaultMinutes);

    /// <summary>
    /// Runs one Porting Studio stage within its budget: "STAGE name start/end" lines with UTC timestamps and the outcome, a
    /// progress line every 30 s naming the stage and the last output line, and the active stage written to --stage-file
    /// (read by the CI watchdog). On timeout: --on-stage-timeout (a diagnostics script) runs first, then the stage is
    /// cancelled through the page's own Cancel; the stage fails — nothing is retried and nothing is skipped silently.
    /// </summary>
    private static async Task<bool> PortStageAsync(PortingStudioViewModel studio, string stage, TimeSpan budget, Func<Task> start)
    {
        var sw = Stopwatch.StartNew();
        void Mark(string state) { if ((Arg("stage-file") ?? Environment.GetEnvironmentVariable("HCG_STAGE_FILE")) is { Length: > 0 } f) File.WriteAllText(f, $"{stage} {state} {DateTimeOffset.UtcNow:O}\n"); }
        Log($"STAGE {stage} start {DateTimeOffset.UtcNow:O} (budget {budget.TotalSeconds:F0} s)");
        Mark("running");
        var command = start();
        var next = TimeSpan.FromSeconds(30);
        while (!(command.IsCompleted && !studio.IsWorking))
        {
            if (sw.Elapsed > budget)
            {
                Log($"STAGE {stage} TIMEOUT {DateTimeOffset.UtcNow:O} after {sw.Elapsed.TotalSeconds:F0} s (budget {budget.TotalSeconds:F0} s) · last output: {_lastOutput ?? "(none)"}");
                Mark("timeout");
                if (Arg("on-stage-timeout") is { } script && File.Exists(script))
                {
                    // Diagnostics while the stalled processes still exist (process tree, stacks, open pipes, Simulator state).
                    using var diag = Process.Start(new ProcessStartInfo(script) { ArgumentList = { stage }, UseShellExecute = false });
                    if (diag is not null && !diag.WaitForExit(TimeSpan.FromMinutes(3))) HCG.ExecutableMaker.Infrastructure.Processes.ProcessTree.Kill(diag);
                }
                studio.CancelCommand.Execute(null);
                var cancelled = Stopwatch.StartNew();
                while (!(command.IsCompleted && !studio.IsWorking) && cancelled.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(200);
                Log($"STAGE {stage} end {DateTimeOffset.UtcNow:O} timeout after {sw.Elapsed.TotalSeconds:F1} s" +
                    (command.IsCompleted && !studio.IsWorking ? " (cancelled)" : " (did not stop within 60 s of Cancel)"));
                Check(false, $"{stage} finished within its {budget.TotalSeconds:F0} s budget");
                return false;
            }
            if (sw.Elapsed > next)
            {
                Log($"… {stage} running for {sw.Elapsed.TotalSeconds:F0} s ({studio.WorkStatus}) · last output: {_lastOutput ?? "(none)"}");
                next += TimeSpan.FromSeconds(30);
            }
            await Task.Delay(200);
        }
        var failed = command.IsFaulted || studio.Error is not null;
        Log($"STAGE {stage} end {DateTimeOffset.UtcNow:O} {(failed ? "failed" : "ok")} after {sw.Elapsed.TotalSeconds:F1} s{(studio.Error is { } e ? ": " + e : "")}");
        Mark(failed ? "failed" : "done");
        return true;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(100);
        }
    }

    private static async Task<int> RunAsync(string output)
    {
        var services = AppClass.Services ?? throw new InvalidOperationException("The app did not initialize its services.");
        _services = services;
        var main = services.GetRequiredService<MainWindowViewModel>();
        _window = new MainWindow { DataContext = main, Width = 1480, Height = double.Parse(Arg("window-height", "1500"), System.Globalization.CultureInfo.InvariantCulture) };
        services.GetRequiredService<MainWindowProvider>().Window = _window;
        _window.Show();
        await services.GetRequiredService<ThemeService>().ApplySavedAsync();
        await main.InitializeAsync();
        var host = HostInfo.Detect();
        Log($"Host: {host.DisplayName} ({host.OsDescription}) · footer: {main.EngineStatus}");
        if (_args.ContainsKey("account-signed-in")) return await RunAccountSignedInAsync(main);

        // Optional: select an Xcode for HCG only (Toolchains › Xcode › Browse…), as a user would, before building.
        await main.NavigateAsync("toolchains", null);
        var toolchainsPage = (ToolchainsViewModel)main.CurrentPage!;
        if (Arg("select-xcode") is { } xcodePath)
        {
            Log($"Selecting Xcode for HCG: {xcodePath}");
            await toolchainsPage.BrowseXcodeCommand.ExecuteAsync(null);
            await WaitUntil(() => !toolchainsPage.IsBusy, TimeSpan.FromMinutes(3), "toolchain re-detection");
            Check(toolchainsPage.HcgSelectionActive, "Xcode selected for HCG only: " + toolchainsPage.XcodeMessage);
            Check(Environment.GetEnvironmentVariable("DEVELOPER_DIR")?.StartsWith(xcodePath.TrimEnd('/'), StringComparison.Ordinal) == true,
                $"DEVELOPER_DIR for HCG's processes is {Environment.GetEnvironmentVariable("DEVELOPER_DIR")}");
        }
        Log($"Java: [{toolchainsPage.JavaState.Text}] {toolchainsPage.JavaPath} · {toolchainsPage.JavaVersion} · {toolchainsPage.JavaSource} · {toolchainsPage.JavaDetail}");
        foreach (var jc in toolchainsPage.JavaCandidates) Log("Java candidate: " + jc);
        Log($"Android SDK: [{toolchainsPage.SdkState.Text}] {toolchainsPage.SdkPath} · platforms {toolchainsPage.SdkPlatforms} · build-tools {toolchainsPage.SdkBuildTools} · {toolchainsPage.SdkDetail}");
        foreach (var grp in toolchainsPage.Groups) foreach (var row in grp.Items) Log($"Tool [{grp.Category}] {row.Name}: {row.Status.Text} · {row.Version} · {row.Location}");
        Shot("toolchains");
        ShotScrolledToEnd("toolchains-groups");
        if (toolchainsPage.HasXcodePanel)
        {
            Log($"Xcode: [{toolchainsPage.XcodeState.Text}] {toolchainsPage.XcodeDiagnosis}");
            Log($"Xcode active: {toolchainsPage.XcodeActivePath} · {toolchainsPage.XcodeSource} · {toolchainsPage.XcodeVersion}");
            Log($"Xcode SDKs: {toolchainsPage.XcodeSdks}");
            foreach (var i in toolchainsPage.XcodeInstallations) Log($"Xcode installed: {i.Title} {i.Path}{(i.IsActive ? " (active)" : "")}");
            if (toolchainsPage.HasXcodeAction) Log("Xcode action: " + toolchainsPage.XcodeAction);
            Shot("toolchains-xcode");
            if (Arg("expect-xcode-state") is { } expectedState)
                Check(toolchainsPage.XcodeState.Text == expectedState, $"Xcode state: expected {expectedState}, got {toolchainsPage.XcodeState.Text}");
        }

        if (_args.ContainsKey("port")) return await PortAsync(main, output, host);

        // 1. Import through the wizard's own "Import ZIP" / "Import folder" commands (scripted picker returns --source).
        var source = _args["source"];
        var isZip = File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        await main.NavigateAsync("new-build", isZip ? BuildWizardViewModel.ImportZipRequest : BuildWizardViewModel.ImportFolderRequest);
        var wizard = (BuildWizardViewModel)main.CurrentPage!;
        await WaitUntil(() => !wizard.IsImporting && (wizard.IsInspectStep || wizard.ImportError is not null), TimeSpan.FromMinutes(5), "import");
        Check(wizard.ImportError is null, $"Imported {(isZip ? "ZIP" : "folder")} {Path.GetFileName(source)}" + (wizard.ImportError is { } e ? ": " + e : ""));
        if (wizard.ImportError is not null) return 1;
        if (_args.ContainsKey("prepare-desktop")) return await RunPrepareDesktopAsync(wizard, host, output);
        if (_args.ContainsKey("electron")) return await RunElectronAsync(wizard, host, output);
        if (_args.ContainsKey("native")) return await RunNativeAsync(wizard, host, output);
        if (_args.ContainsKey("mobile")) return await RunMobileAsync(main, wizard, output);
        if (_args.ContainsKey("account-pages")) return await RunAccountPagesAsync(main, wizard);

        // 2. Inspect: packages listed, none auto-selected when there are several.
        Check(wizard.IsSwiftProject, $"Recognized as Swift project ({wizard.Analysis?.Framework}; support {wizard.Analysis?.Support})");
        var packages = wizard.ProjectOptions.Select(p => p.Path).ToList();
        Log("Discovered packages: " + string.Join(", ", packages));
        if (packages.Count > 1) Check(wizard.SelectedProject is null, "Several packages: none selected automatically");
        Shot("inspect-packages");
        var wantedManifest = _args["package"].Trim('/') is { Length: > 0 } d ? d + "/Package.swift" : "Package.swift";
        var choice = wizard.ProjectOptions.FirstOrDefault(p => p.Path == wantedManifest);
        Check(choice is not null, $"Package {wantedManifest} offered");
        if (choice is null) return 1;
        wizard.SelectedProject = choice;
        Log($"Package status: [{wizard.SwiftPackageTag.Text}] {wizard.SwiftPackageStatus}");
        foreach (var r in wizard.SwiftRemediation) Log("Remediation: " + r);

        if (_args.ContainsKey("expect-blocked-host"))
        {
            Check(host.Platform != TargetPlatform.MacOS, "Blocked-host check runs on a non-Mac host");
            Check(!wizard.SwiftHostCanEvaluate, "Evaluation is not offered on this host");
            Check(!wizard.EvaluateSwiftPackageCommand.CanExecute(null), "Evaluate command disabled");
            Check(!wizard.CanConfigure && !wizard.NextCommand.CanExecute(null), "Cannot continue to Configure");
            Check(wizard.SwiftRemediation.Any(r => r.Contains("Mac", StringComparison.Ordinal)), "Explains that a Mac is required");
            Shot("blocked-host");
            return 0;
        }

        // 3. Trusted evaluation (dump-package via the build worker).
        Check(wizard.SwiftHostCanEvaluate, "Evaluation offered on this Mac");
        Check(!wizard.EvaluateSwiftPackageCommand.CanExecute(null), "Evaluation refused before trust is confirmed");
        wizard.SwiftEvaluationTrusted = true;
        await wizard.EvaluateSwiftPackageCommand.ExecuteAsync(null);
        await WaitUntil(() => !wizard.IsEvaluatingSwift, TimeSpan.FromMinutes(6), "evaluation");
        Check(wizard.SwiftEvaluationError is null && wizard.IsSwiftEvaluated, "Package.swift evaluated" + (wizard.SwiftEvaluationError is { } ee ? ": " + ee : ""));
        foreach (var l in wizard.SwiftEvaluationLog.Take(40)) Log("  eval | " + l);
        var products = wizard.SwiftExecutableProducts.Select(p => $"{p.Name} ({p.KindText})").ToList();
        Log("Executable products: " + string.Join(", ", products));
        Log("Library products: " + wizard.SwiftLibrariesText);
        Log("Test targets: " + wizard.SwiftTestsText);
        ExpectSet("expect-products", wizard.SwiftExecutableProducts.Select(p => p.Name), "Executable products");
        ExpectSet("expect-libraries", wizard.SwiftLibraryProducts, "Library products");
        ExpectSet("expect-tests", wizard.SwiftTestTargets, "Test targets");
        if (wizard.SwiftExecutableProducts.Count > 1) Check(wizard.SelectedSwiftProduct is null, "Several executable products: none selected automatically");
        var product = wizard.SwiftExecutableProducts.FirstOrDefault(p => p.Name == Arg("product"));
        Check(product is not null, $"Product {Arg("product")} available");
        if (product is null) { Shot("evaluated"); return 1; }
        wizard.SelectedSwiftProduct = product;
        Shot("evaluated-products");

        // 4. Configure.
        Check(wizard.NextCommand.CanExecute(null), "Next enabled after choosing the product");
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsConfigureStep && wizard.IsSwiftBuild, "Configure step shows the Swift app options");
        if (Arg("kind") is { } kind)
            wizard.SelectedSwiftKind = wizard.SwiftKindOptions.First(k => k.Value == (kind == "cli" ? SwiftAppKind.CommandLine : SwiftAppKind.GuiApp));
        Log($"Deliver as: {wizard.SelectedSwiftKind?.Name} — {wizard.SwiftKindNote}");
        if (Arg("name") is { } name) wizard.AppName = name;
        if (Arg("publisher") is { } pub) wizard.Publisher = pub;
        if (Arg("bundle-id") is { } id) wizard.AppId = id;
        if (Arg("version") is { } v) wizard.Version = v;
        if (Arg("build-number") is { } bn) wizard.SwiftBuildNumber = bn;
        if (Arg("min-macos") is { } mm) wizard.SwiftMinimumMacOS = mm;
        if (Arg("icon") is { } icon) wizard.IconPath = Path.GetFullPath(icon);
        if (Arg("category") is { } cat) wizard.SelectedSwiftCategory = wizard.SwiftCategoryOptions.First(c => c.Value == cat);
        var arch = Arg("arch", host.Architecture == TargetArchitecture.Arm64 ? "arm64" : "x64") switch
        {
            "universal" => TargetArchitecture.Universal, "x64" or "x86_64" => TargetArchitecture.X64, _ => TargetArchitecture.Arm64,
        };
        wizard.SelectedArchitecture = wizard.ArchitectureOptions.First(a => a.Value == arch);
        wizard.OutputDirectory = Path.Combine(output, "artifacts");
        var detectedUsages = wizard.SwiftUsageDescriptions.Select(u => u.Key).ToList();
        Log("Privacy keys requested by the wizard: " + (detectedUsages.Count == 0 ? "none" : string.Join(", ", detectedUsages)));
        Log("Prefill: " + wizard.SwiftPrefillNote);
        if (wizard.SwiftUsageDescriptions.Any(u => u.Text.Trim().Length == 0))
            Check(wizard.HasConfigErrors, "Empty privacy descriptions block the build");
        foreach (var u in wizard.SwiftUsageDescriptions.Where(u => u.Text.Length > 0)) Log($"  {u.Key} prefilled: {u.Text}");
        foreach (var usage in _args.Where(a => a.Key.StartsWith("usage", StringComparison.Ordinal)).Select(a => a.Value))
        {
            var eq = usage.IndexOf('=');
            var item = wizard.SwiftUsageDescriptions.FirstOrDefault(u => u.Key == usage[..eq]);
            Check(item is not null, $"Wizard asks for {usage[..eq]}");
            if (item is not null) item.Text = usage[(eq + 1)..];
        }
        if (Arg("expect-bundle-id") is { } eb) Check(wizard.AppId == eb, $"Bundle id {wizard.AppId} (expected {eb}, from the project's packaging script)");
        if (Arg("expect-version") is { } ev) Check($"{wizard.Version} ({wizard.SwiftBuildNumber})" == ev, $"Version {wizard.Version} ({wizard.SwiftBuildNumber}) (expected {ev})");
        if (Arg("expect-name") is { } en) Check(wizard.AppName == en, $"Display name {wizard.AppName} (expected {en})");
        // Packaging contract (AppPackaging.json): its commands are shown verbatim and run only once confirmed.
        if (wizard.HasSwiftContract)
        {
            Log("Packaging contract: " + wizard.SwiftContractSummary);
            if (wizard.SwiftContractError is { } contractError) Log("Packaging contract error: " + contractError);
            foreach (var c in wizard.SwiftContractCommands) Log($"Contract command: {c.Description} | {c.Command} | {c.Detail}");
            foreach (var c in wizard.SwiftContractContents) Log("Contract adds: " + c);
        }
        if (Arg("expect-contract-command") is { } expectedCommand)
        {
            Check(wizard.HasSwiftContract && wizard.SwiftContractError is null, "Packaging contract shown in Configure");
            Check(wizard.SwiftContractCommands.Any(c => c.Command == expectedCommand), $"Contract command shown verbatim: {expectedCommand}");
            Check(!wizard.SwiftContractConfirmed, "Contract commands are not confirmed by default");
            Check(wizard.ConfigErrors.Any(e => e.StartsWith("This app needs its helper programs", StringComparison.Ordinal)),
                "Build refused until the contract's commands are confirmed");
            Shot("contract-unconfirmed");
        }
        foreach (var helper in (Arg("expect-contract-helpers") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Check(wizard.SwiftContractContents.Any(c => c.StartsWith(helper + "  ", StringComparison.Ordinal)), $"Contract places {helper}");
        if (_args.ContainsKey("confirm-contract")) wizard.SwiftContractConfirmed = true;
        foreach (var err in wizard.ConfigErrors) Log("Config error: " + err);
        Check(wizard.ConfigErrors.Count == 0, "Configuration valid");
        Shot("configure");
        if (wizard.ConfigErrors.Count > 0) return 1;

        // 5. Package.
        await wizard.NextCommand.ExecuteAsync(null);
        Check(wizard.IsPackageStep, "Package step");
        foreach (var o in wizard.PackagingOptions) Log($"Format {o.Name}: {o.Status.Text}{(o.HasReason ? " — " + o.Reason : "")}");
        var formats = Arg("formats", "app").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f switch
            {
                "app" => PackagingFormat.MacAppBundle, "zip" => PackagingFormat.PortableZip, "dmg" => PackagingFormat.MacDmg,
                "folder" => PackagingFormat.PortableFolder, "pkg" => PackagingFormat.MacPkg, "mas" => PackagingFormat.MacAppStore,
                _ => throw new ArgumentException("Unknown format " + f),
            }).ToHashSet();
        if (wizard.ShowDistribution)
        {
            await WaitUntil(() => wizard.SigningInventoryNote.Length > 0, TimeSpan.FromMinutes(1), "signing inventory");
            Log("Signing inventory: " + wizard.SigningInventoryNote);
            if (Arg("signing") == "devid") wizard.SelectedSigningMode = wizard.SigningModeOptions.First(o => o.Value == MacSigningMode.DeveloperId);
            Log($"Developer ID: [{wizard.DeveloperIdState.Text}] {wizard.DeveloperIdReason}");
            Log($"Mac App Store: [{wizard.AppStoreState.Text}] {wizard.AppStoreReason}");
            foreach (var ent in wizard.DeveloperIdEntitlements) Log($"Hardened-runtime entitlement proposed: {ent.Key} — {ent.Evidence}");
            foreach (var ent in wizard.AppStoreEntitlements) Log($"Sandbox entitlement proposed: {ent.Key} — {ent.Evidence}");
            foreach (var r in wizard.AppStoreReviews) Log("Sandbox review: " + r);
            foreach (var b in wizard.AppStoreBlockers) Log("Sandbox blocker: " + b);
            if (Arg("confirm-entitlements") is not null) wizard.EntitlementsConfirmed = true;
        }
        Check(wizard.PackagingOptions.All(o => o.Option.Availability != PackagingAvailability.NotImplemented), "No format is a milestone placeholder");
        foreach (var o in wizard.PackagingOptions) o.IsSelected = o.IsEnabled && formats.Contains(o.Option.Format);
        foreach (var f in formats)
            Check(wizard.PackagingOptions.Any(o => o.Option.Format == f && o.IsSelected), $"Format {f.DisplayName()} available and selected");
        Shot("package");
        foreach (var de in wizard.DistributionErrors) Log("Distribution: " + de);
        if (wizard.ShowDistribution) ShotScrolledToEnd("distribution");

        // 6. Build (trust confirmation required), then results.
        await wizard.NextCommand.ExecuteAsync(null);
        foreach (var row in wizard.Summary) Log($"Summary {row.Label}: {row.Value}");
        Check(!wizard.StartBuildCommand.CanExecute(null), "Build refused before trust is confirmed");
        wizard.TrustAcknowledged = true;
        Shot("build-summary");
        await wizard.StartBuildCommand.ExecuteAsync(null);
        await WaitUntil(() => !wizard.IsBuilding, TimeSpan.FromMinutes(40), "build");
        var result = wizard.Result;
        Check(result?.Status == BuildStatus.Succeeded, $"Build {result?.Status}: {wizard.ResultDetail}");
        Shot("results");
        if (result is null) return 1;
        foreach (var m in result.Messages.Take(30)) Log($"Message {m.Severity}: {m.Text}");
        foreach (var a in result.Artifacts)
        {
            Log($"Artifact {a.Format.DisplayName()}: {a.Path} · {a.SizeBytes:N0} bytes · sha256 {a.Sha256} · {a.Verification}");
            foreach (var c in a.Checks) Log($"    [{c.Status}] {c.Name}: {c.Detail}");
            if (a.Verification == VerificationStatus.Failed) Failures.Add($"Artifact {a.Path} failed verification");
        }
        // Optional explicit distribution step, exactly as the Results page runs it (confirmation dialog answered "yes").
        if (Arg("test-install") is { } testInstall)
        {
            await WaitUntil(() => wizard.CanTestInstall || !OperatingSystem.IsMacOS(), TimeSpan.FromSeconds(30), "Test install availability");
            Check(wizard.CanTestInstall, "Test install offered for the .pkg");
            wizard.LaunchAfterTestInstall = testInstall == "launch";
            await wizard.TestInstallCommand.ExecuteAsync(null);
            foreach (var step in wizard.DistributionResults) Log($"Test install {step.Name}: [{step.Status.Text}] {step.Detail}");
            Check(wizard.DistributionResults.Count > 0 && wizard.DistributionResults.All(x => x.Status.Tone != "error"), "Test install: every step passed");
            Shot("test-install");
        }
        File.WriteAllText(Path.Combine(output, $"{_prefix}-build.json"), JsonSerializer.Serialize(new
        {
            result.Status, result.FailureReason, result.ToolchainVersions, Settings = result.Settings,
            Artifacts = result.Artifacts.Select(a => new { a.Format, a.Path, a.Sha256, a.SizeBytes, a.Verification, a.Checks }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return result.Status == BuildStatus.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Porting Studio: import → assess → select source → generate (plan + diff) → [compile] → [launch], through the
    /// page's own commands. Expectations: --expect-capability, --expect-compile pass|fail|notpossible, --expect-launch …
    /// </summary>
    private static async Task<int> PortAsync(MainWindowViewModel main, string output, HCG.ExecutableMaker.Domain.HostEnvironment host)
    {
        await main.NavigateAsync("porting", null);
        var studio = (PortingStudioViewModel)main.CurrentPage!;
        // Everything the page logs (xcodebuild, simctl, Gradle, the launch stages) is echoed as it happens, so a stalled
        // command is visible in the CI log with the last line it printed.
        studio.LogLines.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<LogLineItem>() ?? []) { _lastOutput = item.Text; Console.WriteLine($"  | {item.Time} {item.Text}"); }
        };
        studio.SimulatorUdid = Arg("simulator-udid");
        // The fixture apps report their first screen through a test-only readiness signal: --expect-ready SCREEN
        // [--expect-ready-element id=value …]. That signal, not a screenshot, decides whether the launched app is ready.
        if (Arg("expect-ready") is { } readyScreen)
        {
            var elements = _args.Where(x => x.Key.StartsWith("expect-ready-element", StringComparison.Ordinal)).Select(x => x.Value)
                .ToDictionary(v => v[..v.IndexOf('=')], v => v[(v.IndexOf('=') + 1)..], StringComparer.Ordinal);
            studio.Readiness = new HCG.ExecutableMaker.Porting.DeviceLauncher.ReadinessExpectation(readyScreen, elements);
            Log($"Readiness signal expected: screen {readyScreen}{(elements.Count == 0 ? "" : " with " + string.Join(", ", elements.Select(e => $"{e.Key} = \"{e.Value}\"")))}");
        }
        if (studio.SimulatorUdid is { } udid) Log("Simulator selected explicitly: " + udid);
        var source = _args["source"];
        var isZip = File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (!await PortStageAsync(studio, "assessment", Budget("assessment", 10), async () =>
            {
                await (isZip ? studio.ImportZipCommand.ExecuteAsync(null) : studio.ImportFolderCommand.ExecuteAsync(null));
                await WaitUntil(() => !studio.IsWorking && studio.SelectedProject is not null, TimeSpan.FromMinutes(10), "import");
                await Task.Delay(300);
            }))
            return 1;
        Check(studio.Error is null, "Imported and assessed" + (studio.Error is { } e ? ": " + e : ""));
        foreach (var path in studio.Paths)
        {
            Log($"Path {path.Title}: {(path.HasSources ? "" : path.EmptyText)}");
            foreach (var s in path.Sources) Log($"  source {s.Assessment.Source.Key} [{s.Kind}] → {s.Assessment.Capability}: {s.Summary}");
        }
        // The compatibility report (read-only inspection), before any target is chosen.
        if (studio.Compatibility is { } compat)
        {
            Log("Compatibility: " + studio.CompatibilitySummary);
            foreach (var src in compat.Sources) Log($"  source [{src.Framework}] {src.Language} · {src.Directory} · {src.Evidence}");
            foreach (var f in compat.Features) Log($"  feature [{f.Class.DisplayName()}] {f.Area}: {f.Title}{(f.Location is null ? "" : " (" + f.Location + ")")}");
            foreach (var o in compat.Options) Log($"  option {o.SourceKey} → {o.Target.DisplayName()}: {o.Status} · {o.Mode.DisplayName()}{(o.Path is { } p ? " · " + p : "")}{(o.Capability is { } c ? " · " + c : "")}");
            foreach (var b in compat.Blockers) Log("  blocker: " + b);
            Check(studio.Matrix.Count == HCG.ExecutableMaker.Porting.ConversionCatalog.Matrix.Count, "Support matrix shown in full (implemented and planned rows)");
            if (_args.ContainsKey("expect-executable-only"))
            {
                Check(compat.ExecutableOnly && compat.Options.Count == 0 && compat.Blockers.Any(b => b.Contains("cannot be converted", StringComparison.Ordinal)),
                    "An executable alone is refused as conversion source, with the reason");
                Shot("compatibility-executable-only");
                return Failures.Count == 0 ? 0 : 1;
            }
            Shot("compatibility-report");
        }
        else Check(false, "Compatibility report shown");
        var key = _args["port"];
        var chosen = Arg("path") is { } pathArg && Enum.TryParse<HCG.ExecutableMaker.Domain.PortingPath>(pathArg, out var portingPath)
            ? studio.SelectSource(key, portingPath) : studio.SelectSourceByKey(key);
        Check(chosen, $"Source {key} listed{(Arg("path") is { } pa ? " on " + pa : "")}");
        if (studio.SelectedSource is null) { Shot("port-sources"); return 1; }
        var a = studio.SelectedSource.Assessment;
        foreach (var u in a.Reusable) Log($"  reusable {u.Name}: {u.Files} files ({u.Lines} lines) · unchanged {u.FilesUnchanged} · adapted {u.FilesAdapted} · with blockers {u.FilesWithBlockers}");
        foreach (var f in a.Findings) Log($"  [{f.Class.DisplayName()} / {f.Disposition}] {f.Category} {f.Location}: {f.Title}{(f.Guidance is null ? "" : " → " + f.Guidance)}");
        foreach (var m in a.ManualSteps) Log("  manual step: " + m);
        foreach (var t in a.Toolchains) Log($"  toolchain {t.DisplayName}: {(t.IsSatisfied ? "OK" : "MISSING")} {t.Detail}");
        if (Arg("expect-capability") is { } cap) Check(a.Capability.ToString() == cap, $"Capability {a.Capability} (expected {cap})");
        Shot("port-assessment");
        if (_args.ContainsKey("assess-only")) return Failures.Count == 0 ? 0 : 1;

        if (Arg("name") is { } name) studio.AppName = name;
        if (Arg("bundle-id") is { } id) studio.AppId = id;
        if (Arg("args") is { } defArgs) studio.DefaultArguments = defArgs;
        foreach (var usage in _args.Where(x => x.Key.StartsWith("usage", StringComparison.Ordinal)).Select(x => x.Value))
        {
            var eq = usage.IndexOf('=');
            if (studio.RequiredTexts.FirstOrDefault(t => t.Key == usage[..eq]) is { } item) item.Text = usage[(eq + 1)..];
        }
        studio.OutputParent = Path.Combine(output, "ports");
        if (_args.ContainsKey("draft")) studio.Draft = true;
        Check(studio.GenerateCommand.CanExecute(null), "Generate enabled");
        if (!await PortStageAsync(studio, "generation", Budget("generation", 5), () => studio.GenerateCommand.ExecuteAsync(null))) return 1;
        Check(studio.Error is null && studio.Report is not null, "Port generated" + (studio.Error is { } ge ? ": " + ge : ""));
        if (studio.Report is not { } report) { Shot("port-generate-failed"); return 1; }
        foreach (var step in report.Plan.Steps) Log("  plan: " + step);
        foreach (var c in report.Plan.Changes.Where(c => c.Kind != HCG.ExecutableMaker.Domain.FileChangeKind.CopiedUnchanged)) Log($"  {c.Kind}: {c.Path} — {c.Reason}");
        Log("Output: " + report.OutputDirectory);
        Shot("port-plan-diff");
        var hcgPort = Path.Combine(report.OutputDirectory, HCG.ExecutableMaker.Porting.PortingService.ReportFolder);
        Check(File.Exists(Path.Combine(hcgPort, "MIGRATION.md")) && File.Exists(Path.Combine(hcgPort, "conversion.json")), "Migration report and conversion record written");
        Check(report.Source is not null, "Source revision recorded: " + report.Source?.Describe());

        if (_args.ContainsKey("validate"))
        {
            studio.TrustAcknowledged = true;
            if (!await PortStageAsync(studio, "validation", Budget("validation", 15), () => studio.ValidateCommand.ExecuteAsync(null))) return 1;
            foreach (var c in studio.ValidationChecks) Log($"  validate {(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
            var v = studio.Report!.Level(HCG.ExecutableMaker.Domain.VerificationLevel.Validated);
            Log($"Validated: {v.Status} — {v.Detail}");
            if (Arg("expect-validate") is { } ev) Check(v.Status.ToString() == ev, $"Validate status {v.Status} (expected {ev})");
            ShotScrolledToEnd("port-validated");
        }
        if (Arg("expect-text") is { } text) studio.ExpectText = text;

        if (_args.ContainsKey("compile"))
        {
            studio.TrustAcknowledged = true;
            if (!await PortStageAsync(studio, "compilation", Budget("compilation", 12), () => studio.CompileCommand.ExecuteAsync(null))) return 1;
            foreach (var l in studio.LogLines.Where(l => l.IsError || l.IsWarning).Take(40)) Log("  build: " + l.Text);
        }
        if (_args.ContainsKey("launch") && studio.Report!.Level(HCG.ExecutableMaker.Domain.VerificationLevel.Compiled).Status == HCG.ExecutableMaker.Domain.LevelStatus.Passed)
        {
            // Boot, install, launch and ready are stages of their own inside (each bounded; see the "STAGE" lines).
            if (!await PortStageAsync(studio, "launch", Budget("launch", 25), () => studio.LaunchCommand.ExecuteAsync(null))) return 1;
        }
        report = studio.Report!;
        foreach (var v in report.Verification.OrderBy(v => v.Level)) Log($"VERIFY {v.Level}: {v.Status} — {v.Detail}{(v.Evidence is null ? "" : " [" + v.Evidence + "]")}");
        if (Arg("expect-compile") is { } ec) Check(report.Level(HCG.ExecutableMaker.Domain.VerificationLevel.Compiled).Status.ToString() == ec, $"Compile status {report.Level(HCG.ExecutableMaker.Domain.VerificationLevel.Compiled).Status} (expected {ec})");
        if (Arg("expect-launch") is { } el) Check(report.Level(HCG.ExecutableMaker.Domain.VerificationLevel.LaunchedInSimulatorOrEmulator).Status.ToString() == el, $"Launch status {report.Level(HCG.ExecutableMaker.Domain.VerificationLevel.LaunchedInSimulatorOrEmulator).Status} (expected {el})");
        Check(report.Level(HCG.ExecutableMaker.Domain.VerificationLevel.TestedOnDevice).Status != HCG.ExecutableMaker.Domain.LevelStatus.Passed || _args.ContainsKey("device"), "Device testing is not claimed without a physical device");
        Shot("port-verification");
        if (Arg("copy-port-to") is { } copyTo)
        {
            Directory.CreateDirectory(copyTo);
            File.WriteAllText(Path.Combine(copyTo, "port-output-path.txt"), report.OutputDirectory);
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void ExpectSet(string arg, IEnumerable<string> actual, string what)
    {
        if (Arg(arg) is not { } expected) return;
        var want = expected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order().ToList();
        var got = actual.Order().ToList();
        Check(want.SequenceEqual(got), $"{what}: expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
    }

    private sealed class ScriptedShell(string source) : IPlatformShell
    {
        public Task<string?> PickZipAsync() => Task.FromResult<string?>(File.Exists(source) ? source : null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(Directory.Exists(source) ? source : null);
        public Task<string?> PickIconAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string typeName, params string[] patterns) => Task.FromResult(Arg("pick-file"));
        public Task CopyAsync(string text) => Task.CompletedTask;
        public Task<bool> OpenFolderAsync(string path) => Task.FromResult(Directory.Exists(path));
        public Task<string?> PickApplicationAsync(string title) => Task.FromResult(Arg("select-xcode"));
        public Task<bool> OpenApplicationAsync(string appPath) => Task.FromResult(false);
        public Task<bool> OpenUrlAsync(string url) => Task.FromResult(OpenUrl(url));
    }

    private sealed class LoggingDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false)
        {
            Log($"Dialog (confirmed) {title}: {message}");
            return Task.FromResult(true);
        }

        public Task AlertAsync(string title, string message)
        {
            Log($"Dialog (alert) {title}: {message}");
            Failures.Add($"Unexpected alert: {title}: {message}");
            return Task.CompletedTask;
        }

        public Task ShowLogAsync(string title, string path) => Task.CompletedTask;
    }
}
