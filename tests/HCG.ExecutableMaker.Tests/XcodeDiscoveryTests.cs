using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Xcode discovery, diagnosis and HCG's app-specific developer directory (no Mac needed: fake Xcode.app trees).</summary>
[Collection("DeveloperDir")]
public sealed class XcodeDiscoveryTests
{
    internal static string FakeXcode(TempDir dir, string name, string version, string build, string bundleId = "com.apple.dt.Xcode")
    {
        var app = dir.Combine(name);
        dir.Write($"{name}/Contents/Info.plist", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleIdentifier</key><string>{bundleId}</string>
              <key>CFBundleShortVersionString</key><string>{version}</string>
            </dict></plist>
            """);
        dir.Write($"{name}/Contents/version.plist", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>ProductBuildVersion</key><string>{build}</string></dict></plist>
            """);
        dir.Write($"{name}/Contents/Developer/usr/bin/xcodebuild", "#!/bin/sh\n");
        return app;
    }

    [Fact]
    public void Finds_Xcode_apps_newest_first_and_ignores_lookalikes()
    {
        using var dir = new TempDir();
        FakeXcode(dir, "Xcode.app", "27.2", "27B52");
        FakeXcode(dir, "Xcode-26.4.app", "26.4", "26E100");
        FakeXcode(dir, "XcodeHelper.app", "1.0", "1A1", bundleId: "com.example.helper");
        Directory.CreateDirectory(dir.Combine("Xcode-broken.app", "Contents"));   // no Info.plist / xcodebuild

        var found = XcodeDiscovery.FindInstallations([dir.Path]);

        Assert.Equal(["27.2", "26.4"], found.Select(x => x.Version));
        Assert.Equal("27B52", found[0].BuildVersion);
        Assert.Equal(Path.Combine(dir.Path, "Xcode.app", "Contents", "Developer"), found[0].DeveloperDirectory);
    }

    [Fact]
    public void Version_comes_from_version_plist_when_Info_plist_is_binary()
    {
        using var dir = new TempDir();
        var app = FakeXcode(dir, "Xcode_26.5.app", "26.5", "17F42");
        // Real Xcode ships a binary Info.plist; only version.plist is XML.
        File.WriteAllBytes(Path.Combine(app, "Contents", "Info.plist"), [.. "bplist00"u8.ToArray(), 0, 1, 2, 3]);
        File.WriteAllText(Path.Combine(app, "Contents", "version.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>CFBundleShortVersionString</key><string>26.5</string><key>ProductBuildVersion</key><string>17F42</string></dict></plist>
            """);
        var x = XcodeDiscovery.Read(app)!;
        Assert.Equal(("26.5", "17F42"), (x.Version, x.BuildVersion));
        Assert.StartsWith("Xcode 26.5 (17F42)", x.Display);
    }

    [UnixFact]
    public void Symlinked_names_of_the_same_Xcode_are_listed_once_keeping_the_active_name()
    {
        using var dir = new TempDir();
        var real = FakeXcode(dir, "Xcode_26.6.0.app", "26.6", "17F113");
        FakeXcode(dir, "Xcode_26.5.app", "26.5", "17F42");
        Directory.CreateSymbolicLink(dir.Combine("Xcode.app"), real);
        Directory.CreateSymbolicLink(dir.Combine("Xcode_26.6.app"), real);

        var plain = XcodeDiscovery.FindInstallations([dir.Path]);
        Assert.Equal([real, dir.Combine("Xcode_26.5.app")], plain.Select(x => x.AppPath));

        var activeAlias = XcodeDiscovery.FindInstallations([dir.Path], [dir.Combine("Xcode.app", "Contents", "Developer")], dir.Combine("Xcode.app", "Contents", "Developer"));
        Assert.Equal([dir.Combine("Xcode.app"), dir.Combine("Xcode_26.5.app")], activeAlias.Select(x => x.AppPath));
    }

    [Fact]
    public void Read_accepts_the_app_or_its_developer_directory_and_deduplicates()
    {
        using var dir = new TempDir();
        var app = FakeXcode(dir, "Xcode.app", "27.2", "27B52");
        Assert.Equal(app, XcodeDiscovery.Read(Path.Combine(app, "Contents", "Developer"))!.AppPath);
        Assert.Equal(app, XcodeDiscovery.Read(app + "/")!.AppPath);
        Assert.Null(XcodeDiscovery.Read(dir.Combine("missing.app")));
        // Spotlight results that repeat a folder hit are de-duplicated.
        Assert.Single(XcodeDiscovery.FindInstallations([dir.Path], [app]));
    }

    private static XcodeDiscovery.ProbeResults Probe(string? active, IReadOnlyList<XcodeInstallation> installed, int exit = 0, string output = "Xcode 27.2\nBuild version 27B52\n",
        string error = "", int? firstLaunch = 0, IReadOnlyList<string>? sdks = null, DeveloperDirectorySource source = DeveloperDirectorySource.XcodeSelect) =>
        new(active, source, installed, exit, output, error, firstLaunch, sdks ?? ["macosx 27.1", "iphoneos 27.1", "iphonesimulator 27.1"]);

    private static XcodeInstallation Installed(string path = "/Applications/Xcode.app") => new(path, path + "/Contents/Developer", "27.2", "27B52");

    [Fact]
    public void Command_Line_Tools_selected_with_Xcode_installed_is_installed_not_selected_with_an_HCG_only_action()
    {
        var s = XcodeDiscovery.Diagnose(Probe("/Library/Developer/CommandLineTools", [Installed()], exit: 1, output: "",
            error: "xcode-select: error: tool 'xcodebuild' requires Xcode, but active developer directory '/Library/Developer/CommandLineTools' is a command line tools instance"));
        Assert.Equal(XcodeState.InstalledNotSelected, s.State);
        Assert.Contains("Xcode 27.2 (27B52) — /Applications/Xcode.app", s.Diagnosis);
        Assert.Contains("Command Line Tools are enough for macOS Swift package builds", s.Diagnosis);
        Assert.StartsWith("Select Xcode 27.2 (27B52) for HCG", s.CorrectiveAction);
        Assert.Contains("the system setting stays as it is", s.CorrectiveAction);
        Assert.Contains("sudo xcode-select --switch \"/Applications/Xcode.app/Contents/Developer\"", s.CorrectiveAction);
        Assert.Null(s.VerifiedVersion);   // nothing was verified for the CLT
        Assert.NotNull(s.RawError);
    }

    [Fact]
    public void Nothing_installed_points_to_the_App_Store_and_Browse()
    {
        var s = XcodeDiscovery.Diagnose(Probe("/Library/Developer/CommandLineTools", [], exit: 1, output: ""));
        Assert.Equal(XcodeState.NotInstalled, s.State);
        Assert.Contains(XcodeDiscovery.AppStoreUrl, s.CorrectiveAction);
        Assert.Contains("Browse", s.CorrectiveAction);
    }

    [Fact]
    public void License_first_launch_sdks_and_ready_are_distinguished()
    {
        var dev = "/Applications/Xcode.app/Contents/Developer";
        var license = XcodeDiscovery.Diagnose(Probe(dev, [Installed()], exit: 69, output: "",
            error: "You have not agreed to the Xcode license agreements. Please run 'sudo xcodebuild -license' from within a Terminal window to review and agree to the Xcode and Apple SDKs license."));
        Assert.Equal(XcodeState.LicenseNotAccepted, license.State);
        Assert.Contains($"sudo \"{dev}/usr/bin/xcodebuild\" -license accept", license.CorrectiveAction);

        var firstLaunch = XcodeDiscovery.Diagnose(Probe(dev, [Installed()], firstLaunch: 1));
        Assert.Equal(XcodeState.FirstLaunchRequired, firstLaunch.State);
        Assert.Contains("-runFirstLaunch", firstLaunch.CorrectiveAction);
        Assert.Equal("27.2", firstLaunch.VerifiedVersion);

        var noSdks = XcodeDiscovery.Diagnose(Probe(dev, [Installed()], sdks: []));
        Assert.Equal(XcodeState.SdkMissing, noSdks.State);

        var macOnly = XcodeDiscovery.Diagnose(Probe(dev, [Installed()], sdks: ["macosx 27.1"]));
        Assert.Equal(XcodeState.Ready, macOnly.State);
        Assert.Contains("iOS platform is not installed", macOnly.Diagnosis);
        Assert.Contains("Components", macOnly.CorrectiveAction);

        var ready = XcodeDiscovery.Diagnose(Probe(dev, [Installed()]));
        Assert.Equal(XcodeState.Ready, ready.State);
        Assert.Equal(("27.2", "27B52"), (ready.VerifiedVersion, ready.VerifiedBuild));
        Assert.Equal("", ready.CorrectiveAction);

        var broken = XcodeDiscovery.Diagnose(Probe(dev, [Installed()], exit: 1, output: "", error: "xcodebuild: error: unable to load plug-in"));
        Assert.Equal(XcodeState.ValidationFailed, broken.State);
        Assert.Equal("xcodebuild: error: unable to load plug-in", broken.RawError);
    }

    [Fact]
    public void A_vanished_HCG_selection_is_a_validation_failure_with_a_way_back()
    {
        var s = XcodeDiscovery.Diagnose(Probe("/nonexistent/Xcode.app/Contents/Developer", [Installed()], source: DeveloperDirectorySource.HcgSelection));
        Assert.Equal(XcodeState.ValidationFailed, s.State);
        Assert.Contains("no longer exists", s.Diagnosis);
    }

    [Fact]
    public void Toolchain_row_carries_the_exact_action()
    {
        var s = XcodeDiscovery.Diagnose(Probe("/Library/Developer/CommandLineTools", [Installed()], exit: 1, output: ""));
        var row = ToolchainDetector.XcodeToolchainInfo(s);
        Assert.Equal(ToolchainStatus.Missing, row.Status);
        Assert.Equal(s.CorrectiveAction, row.InstallHint);
        Assert.Contains("/Applications/Xcode.app", row.AdditionalVersions[0]);
    }

    [Fact]
    public async Task Selection_is_persisted_and_applied_as_DEVELOPER_DIR_without_touching_xcode_select()
    {
        using var dir = new TempDir();
        var app = FakeXcode(dir, "Xcode.app", "27.2", "27B52");
        var store = new MemorySettingsStore();
        var before = Environment.GetEnvironmentVariable(DeveloperDirectorySelection.Variable);
        var selection = new DeveloperDirectorySelection(store);
        try
        {
            var x = await selection.SelectAsync(app);
            Assert.Equal(Path.Combine(app, "Contents", "Developer"), x.DeveloperDirectory);
            Assert.Equal(x.DeveloperDirectory, (await store.LoadAsync()).DeveloperDirectory);
            Assert.Equal(x.DeveloperDirectory, Environment.GetEnvironmentVariable(DeveloperDirectorySelection.Variable));
            Assert.Equal(DeveloperDirectorySource.HcgSelection, selection.CurrentSource);

            // A fresh process (new selection object) restores the persisted choice.
            DeveloperDirectorySelection.ResetForTests();
            var restored = new DeveloperDirectorySelection(store);
            await restored.InitializeAsync();
            Assert.Equal(x.DeveloperDirectory, restored.Selected);

            await Assert.ThrowsAsync<InvalidOperationException>(() => restored.SelectAsync(dir.Combine("NotXcode.app")));
            Assert.Equal(x.DeveloperDirectory, restored.Selected);   // a bad choice leaves the old one in place

            await restored.ClearAsync();
            Assert.Null((await store.LoadAsync()).DeveloperDirectory);
            Assert.Equal(DeveloperDirectorySelection.LaunchValue, Environment.GetEnvironmentVariable(DeveloperDirectorySelection.Variable));
        }
        finally
        {
            DeveloperDirectorySelection.ResetForTests();
            Environment.SetEnvironmentVariable(DeveloperDirectorySelection.Variable, before);
        }
    }

    [Fact]
    public void The_build_worker_environment_inherits_HCGs_developer_directory()
    {
        var before = Environment.GetEnvironmentVariable(DeveloperDirectorySelection.Variable);
        try
        {
            Environment.SetEnvironmentVariable(DeveloperDirectorySelection.Variable, "/Applications/Xcode.app/Contents/Developer");
            var env = HCG.ExecutableMaker.Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment;
            Assert.Equal("/Applications/Xcode.app/Contents/Developer", env[DeveloperDirectorySelection.Variable]);
        }
        finally { Environment.SetEnvironmentVariable(DeveloperDirectorySelection.Variable, before); }
    }
}

[CollectionDefinition("DeveloperDir", DisableParallelization = true)]
public sealed class DeveloperDirCollection;

internal sealed class MemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { _settings = settings; return Task.CompletedTask; }
}
