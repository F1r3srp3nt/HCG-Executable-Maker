using HCG.ExecutableMaker.BuildEngine.Readiness;
using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using SwiftCapabilities = HCG.ExecutableMaker.ProjectInspection.Swift.SwiftCapabilities;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Build Readiness Center, with HCG Server's packaging contract (Go host agent as a universal helper) as the first
/// regression case: Go is found on PATH, in Apple Silicon and Intel Homebrew, in the official installer location and
/// at a chosen location; versions are read correctly; an older installation is never replaced; installs need approval.
/// </summary>
public sealed class BuildReadinessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hcg-readiness-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ---------- a fake Mac ----------

    private sealed class FakeMac
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal); // path → `<tool> version` output
        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal);
        public string Path { get; set; } = "/usr/bin:/bin:/usr/sbin:/sbin"; // what an app opened from Finder gets
        public bool DeveloperTools { get; set; } = true;

        public ToolHost Host => new()
        {
            Platform = TargetPlatform.MacOS, Home = "/Users/owner",
            GetEnvironment = n => n == "PATH" ? Path : null,
            FileExists = p => Files.ContainsKey(p) || Dirs.Contains(p),
            ListDirectory = d => Files.Keys.Concat(Dirs).Where(f => System.IO.Path.GetDirectoryName(f) == d).Distinct().ToList(),
            LinkTarget = p => Links.GetValueOrDefault(p),
            IsExecutable = p => Files.ContainsKey(p),
            AppleDeveloperToolsPresent = () => DeveloperTools,
            IsRoot = () => false,
        };
    }

    private sealed class ProbeRunner(FakeMac mac) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken ct)
        {
            Calls.Add(spec);
            if (!mac.Files.TryGetValue(spec.Executable, out var output)) return Task.FromResult(new ProcessResult(127, TimeSpan.Zero, false, false));
            onOutput?.Invoke(OutputStream.StdOut, output);
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
        }
    }

    private static async Task<ToolResolution> Go(FakeMac mac, string? min = "1.25", IReadOnlyList<PreferredTool>? preferred = null, ProbeRunner? runner = null) =>
        await new ToolResolver(runner ?? new ProbeRunner(mac), mac.Host).ResolveAsync(ToolCatalog.Go, min, preferred ?? [], CancellationToken.None);

    [Fact]
    public async Task Go_on_PATH_is_found_with_its_version()
    {
        var mac = new FakeMac { Path = "/Users/owner/tools/bin:/usr/bin:/bin" };
        mac.Files["/Users/owner/tools/bin/go"] = "go version go1.25.4 darwin/arm64";
        var r = await Go(mac);
        Assert.Equal(ReadinessState.Ready, r.State);
        Assert.Equal("/Users/owner/tools/bin/go", r.Selected!.Path);
        Assert.Equal("1.25.4", r.Selected.Version);
        Assert.Contains("PATH", r.Selected.Source);
    }

    [Fact]
    public async Task Go_from_Apple_Silicon_Homebrew_is_found_when_the_app_was_opened_from_Finder()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/go"] = "go version go1.25.4 darwin/arm64";
        mac.Links["/opt/homebrew/bin/go"] = "/opt/homebrew/Cellar/go/1.25.4/libexec/bin/go";
        var r = await Go(mac);
        Assert.Equal(ReadinessState.Ready, r.State);
        Assert.Equal("/opt/homebrew/bin/go", r.Selected!.Path);
        Assert.Equal("Homebrew (Apple Silicon)", r.Selected.Source);
    }

    [Theory]
    [InlineData("/usr/local/bin/go", "/usr/local/Cellar/go/1.25.4/libexec/bin/go")]
    [InlineData("/usr/local/opt/go/libexec/bin/go", null)]
    public async Task Go_from_Intel_Homebrew_is_found(string path, string? link)
    {
        var mac = new FakeMac();
        mac.Files[path] = "go version go1.25.4 darwin/amd64";
        if (link is not null) mac.Links[path] = link;
        var r = await Go(mac);
        Assert.Equal(ReadinessState.Ready, r.State);
        Assert.Equal(path, r.Selected!.Path);
        Assert.Equal("Homebrew (Intel)", r.Selected.Source);
    }

    [Fact]
    public async Task Go_from_the_official_installer_and_golang_dl_sdks_are_found()
    {
        var mac = new FakeMac();
        mac.Files["/Users/owner/sdk/go1.25.4/bin/go"] = "go version go1.25.4 darwin/arm64";
        mac.Files["/usr/local/go/bin/go"] = "go version go1.24.7 darwin/arm64";
        mac.Dirs.Add("/Users/owner/sdk/go1.25.4");
        var r = await Go(mac);
        Assert.Equal(ReadinessState.Ready, r.State);
        Assert.Equal("/Users/owner/sdk/go1.25.4/bin/go", r.Selected!.Path);
        Assert.Equal("golang.org/dl SDK", r.Selected.Source);
        Assert.Contains(r.Found, f => f.Path == "/usr/local/go/bin/go" && f.Source == "Official Go installer" && f.Version == "1.24.7");
    }

    [Fact]
    public async Task An_older_Go_first_on_PATH_is_kept_and_a_compatible_installation_is_used_instead()
    {
        var mac = new FakeMac { Path = "/usr/local/bin:/usr/bin:/bin" };
        mac.Files["/usr/local/bin/go"] = "go version go1.22.5 darwin/arm64";
        mac.Files["/usr/local/go/bin/go"] = "go version go1.25.0 darwin/arm64";
        var r = await Go(mac);
        Assert.Equal(ReadinessState.Ready, r.State);
        Assert.Equal("/usr/local/go/bin/go", r.Selected!.Path);
        Assert.Equal(2, r.Found.Count);
    }

    [Fact]
    public async Task Only_older_Go_installations_make_the_requirement_incompatible_with_every_version_listed()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/go"] = "go version go1.22.5 darwin/arm64";
        mac.Files["/usr/local/go/bin/go"] = "go version go1.24.7 darwin/arm64";
        var r = await Go(mac);
        Assert.Equal(ReadinessState.IncompatibleVersion, r.State);
        Assert.Null(r.Selected);
        Assert.Contains("1.22.5", r.Detail);
        Assert.Contains("1.24.7", r.Detail);
    }

    [Fact]
    public async Task No_Go_anywhere_is_NotFound_and_names_where_HCG_looked()
    {
        var r = await Go(new FakeMac());
        Assert.Equal(ReadinessState.NotFound, r.State);
        Assert.Contains("/opt/homebrew/bin", r.Detail);
        Assert.Contains("/usr/local/go/bin", r.Detail);
    }

    [Fact]
    public async Task A_chosen_Go_location_wins_over_PATH_and_is_never_silently_replaced()
    {
        var mac = new FakeMac { Path = "/opt/homebrew/bin:/usr/bin" };
        mac.Files["/opt/homebrew/bin/go"] = "go version go1.26.0 darwin/arm64";
        mac.Files["/Volumes/Tools/go1.25/bin/go"] = "go version go1.25.4 darwin/arm64";
        var chosen = await Go(mac, preferred: [new PreferredTool("/Volumes/Tools/go1.25/bin/go", "Profile “HCG Server”")]);
        Assert.Equal("/Volumes/Tools/go1.25/bin/go", chosen.Selected!.Path);
        Assert.Equal("Profile “HCG Server”", chosen.Selected.Source);

        var missing = await Go(mac, preferred: [new PreferredTool("/Volumes/Gone/bin/go", "Profile “HCG Server”")]);
        Assert.Equal(ReadinessState.InvalidPath, missing.State); // not the Homebrew Go on PATH
        Assert.Null(missing.Selected);
    }

    [Fact]
    public async Task Choosing_a_folder_finds_the_executable_and_rejects_the_wrong_tool()
    {
        var mac = new FakeMac();
        mac.Files["/Users/owner/go-root/bin/go"] = "go version go1.25.4 darwin/arm64";
        mac.Files["/opt/homebrew/bin/node"] = "v22.11.0";
        var resolver = new ToolResolver(new ProbeRunner(mac), mac.Host);
        var ok = await resolver.VerifyChosenAsync(ToolCatalog.Go, "/Users/owner/go-root", "1.25", CancellationToken.None);
        Assert.Equal(ReadinessState.Ready, ok.State);
        Assert.Equal("/Users/owner/go-root/bin/go", ok.Selected!.Path);
        var wrong = await resolver.VerifyChosenAsync(ToolCatalog.Go, "/opt/homebrew/bin/node", null, CancellationToken.None);
        Assert.Equal(ReadinessState.InvalidPath, wrong.State);
        Assert.Contains("not go", wrong.Detail);
    }

    [Fact]
    public async Task The_Go_version_probe_never_downloads_a_toolchain_and_never_runs_in_the_project()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/go"] = "go version go1.25.4 darwin/arm64";
        var runner = new ProbeRunner(mac);
        await Go(mac, runner: runner);
        var probe = Assert.Single(runner.Calls);
        Assert.Equal(["version"], probe.Arguments);
        Assert.Equal("local", probe.Environment!["GOTOOLCHAIN"]);
        Assert.Equal(Path.GetTempPath(), probe.WorkingDirectory);
    }

    [Fact]
    public async Task Apple_shims_are_not_run_without_the_developer_tools()
    {
        var mac = new FakeMac { DeveloperTools = false };
        mac.Files["/usr/bin/lipo"] = "";
        var runner = new ProbeRunner(mac);
        var r = await new ToolResolver(runner, mac.Host).ResolveAsync(ToolCatalog.Lipo, null, [], CancellationToken.None);
        Assert.Equal(ReadinessState.SetupRequired, r.State);
        Assert.Empty(runner.Calls);
        mac.DeveloperTools = true;
        r = await new ToolResolver(runner, mac.Host).ResolveAsync(ToolCatalog.Lipo, null, [], CancellationToken.None);
        Assert.Equal(ReadinessState.Ready, r.State);
    }

    [Theory]
    [InlineData("go", "go version go1.25.4 darwin/arm64", "1.25.4")]
    [InlineData("go", "go version go1.26rc1 darwin/arm64", "1.26")]
    [InlineData("swift", "swift-driver version: 1.120.5 Apple Swift version 6.1.2 (swiftlang-6.1.2.1.2 clang-1700.0.13.5)", "6.1.2")]
    [InlineData("dotnet", "8.0.404 [/usr/local/share/dotnet/sdk]\n10.0.100 [/usr/local/share/dotnet/sdk]\n9.0.101 [/usr/local/share/dotnet/sdk]", "10.0.100")]
    [InlineData("cmake", "cmake version 3.31.2\n\nCMake suite maintained", "3.31.2")]
    [InlineData("node", "v22.11.0", "22.11.0")]
    [InlineData("python3", "Python 3.13.1", "3.13.1")]
    [InlineData("cargo", "cargo 1.83.0 (5ffbef321 2024-10-29)", "1.83.0")]
    [InlineData("docker", "Docker version 27.3.1, build ce12230", "27.3.1")]
    [InlineData("javac", "javac 21.0.5", "21.0.5")]
    public void Versions_are_read_from_each_tools_own_output(string tool, string output, string expected) =>
        Assert.Equal(expected, ToolResolver.Version(ToolCatalog.For(tool), output));

    [Fact]
    public void The_catalogue_covers_the_toolchains_the_readiness_center_supports()
    {
        foreach (var id in new[] { "go", "swift", "dotnet", "node", "npm", "python3", "rustc", "cargo", "javac", "cmake", "ninja", "clang", "gcc", "git", "docker", "lipo", "make" })
            Assert.NotNull(ToolCatalog.Find(id));
        Assert.Equal("go", ToolCatalog.For("golang").Id);
        Assert.True(ToolCatalog.For("hcg-codegen").Generic);
    }

    // ---------- installing ----------

    [Fact]
    public void Homebrew_install_is_offered_only_as_an_approved_catalogue_formula_that_upgrades_nothing()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/brew"] = "";
        var options = new DependencyInstaller(new ProbeRunner(mac), mac.Host).Options(ToolCatalog.Go, ReadinessState.NotFound, "1.25");
        var brew = Assert.Single(options, o => o.Runnable);
        Assert.Equal(InstallMethod.Homebrew, brew.Method);
        Assert.Equal("/opt/homebrew/bin/brew", brew.Executable);
        Assert.Equal(["install", "go"], brew.Arguments);
        Assert.Equal("1", brew.Environment["HOMEBREW_NO_INSTALL_UPGRADE"]);
        Assert.Equal("1", brew.Environment["HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK"]);
        Assert.Contains(options, o => o.Method == InstallMethod.Official && o.Url == "https://go.dev/dl/");
    }

    [Fact]
    public void Intel_Homebrew_is_used_when_it_is_the_only_Homebrew()
    {
        var mac = new FakeMac();
        mac.Files["/usr/local/bin/brew"] = "";
        var brew = new DependencyInstaller(new ProbeRunner(mac), mac.Host).Options(ToolCatalog.Go, ReadinessState.NotFound, null).Single(o => o.Runnable);
        Assert.Equal("/usr/local/bin/brew", brew.Executable);
        Assert.Contains("Intel", brew.Label);
    }

    [Fact]
    public void An_installed_but_older_Homebrew_formula_is_never_upgraded_by_HCG()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/brew"] = "";
        mac.Dirs.Add("/opt/homebrew/opt/go");
        var options = new DependencyInstaller(new ProbeRunner(mac), mac.Host).Options(ToolCatalog.Go, ReadinessState.IncompatibleVersion, "1.25");
        Assert.DoesNotContain(options, o => o.Runnable);
        Assert.Contains(options, o => o.CommandLine == "brew upgrade go" && o.Note!.Contains("does not upgrade", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_Homebrew_HCG_explains_and_never_installs_Homebrew()
    {
        var mac = new FakeMac();
        var options = new DependencyInstaller(new ProbeRunner(mac), mac.Host).Options(ToolCatalog.Go, ReadinessState.NotFound, null);
        Assert.DoesNotContain(options, o => o.Runnable);
        Assert.Contains(options, o => o.Url == "https://brew.sh" && o.Note!.Contains("does not install Homebrew", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_a_project_names_but_HCG_does_not_know_is_never_installed()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/brew"] = "";
        Assert.Empty(new DependencyInstaller(new ProbeRunner(mac), mac.Host).Options(ToolCatalog.For("evil-formula"), ReadinessState.NotFound, null));
    }

    [Fact]
    public async Task Installing_needs_approval_and_only_runs_catalogue_formulae()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/brew"] = "";
        var runner = new ProbeRunner(mac);
        var installer = new DependencyInstaller(runner, mac.Host);
        var brew = installer.Options(ToolCatalog.Go, ReadinessState.NotFound, null).Single(o => o.Runnable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(brew, approved: false, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(brew with { Arguments = ["install", "evil-formula"] }, approved: true, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(brew with { Arguments = ["install", "--HEAD", "go"] }, approved: true, _ => { }, CancellationToken.None));
        Assert.Empty(runner.Calls);
        await installer.InstallAsync(brew, approved: true, _ => { }, CancellationToken.None);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/opt/homebrew/bin/brew", call.Executable);
        Assert.Equal(["install", "go"], call.Arguments);
        Assert.Equal("1", call.Environment!["HOMEBREW_NO_INSTALL_UPGRADE"]);
    }

    // ---------- HCG Server's packaging contract ----------

    private string HcgServerLayout()
    {
        void W(string rel, string text)
        {
            var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        W("go.mod", "module github.com/f1r3srp3nt/hcg-server\n\ngo 1.25.0\n\ntoolchain go1.25.4\n");
        W("cmd/hcg-agent/main.go", "package main\nfunc main() {}\n");
        W("scripts/build-agent-helper.sh", "#!/usr/bin/env bash\ncommand -v go >/dev/null || exit 1\ncommand -v lipo >/dev/null || exit 1\n");
        W("macos/HCGServerApp/Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\n");
        W("macos/HCGServerApp/Resources/Info.plist", "<plist/>");
        W("macos/HCGServerApp/AppPackaging.json", """
            {
              "schema": "hcg-app-packaging/1",
              "product": "HCGServer",
              "prebuild": [
                {
                  "description": "Build the host agent (Go) as a universal helper",
                  "requires": ["go >= 1.25"],
                  "workingDirectory": "../..",
                  "command": ["scripts/build-agent-helper.sh", "--output", "${HELPERS_DIR}/hcg-agent"]
                }
              ],
              "helpers": [
                { "name": "hcg-agent", "source": "${HELPERS_DIR}/hcg-agent", "destination": "Contents/Helpers/hcg-agent",
                  "required": true, "architectures": ["arm64", "x86_64"] }
              ]
            }
            """);
        return _root;
    }

    [Fact]
    public void HCG_Servers_contract_requires_Go_from_the_contract_and_go_mod_and_lipo_for_the_universal_helper()
    {
        var root = HcgServerLayout();
        var (contract, error) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        Assert.Null(error);
        var reqs = BuildRequirementsInspector.Merge(BuildRequirementsInspector.FromContract(root, contract!));
        var go = Assert.Single(reqs, r => r.ToolId == "go");
        Assert.True(go.Required);
        Assert.Equal(0, SwiftCapabilities.Compare(go.MinimumVersion!, "1.25.0")); // the contract's ">= 1.25" and go.mod's "go 1.25.0"
        Assert.Contains("macos/HCGServerApp/AppPackaging.json › prebuild “Build the host agent (Go) as a universal helper”", go.DeclaredBy);
        Assert.Contains("go.mod", go.DeclaredBy);
        Assert.Contains(go.Notes, n => n.Contains("toolchain go1.25.4", StringComparison.Ordinal));
        var lipo = Assert.Single(reqs, r => r.ToolId == "lipo");
        Assert.True(lipo.Required); // the helper must contain arm64 and x86_64
        Assert.Contains("helper hcg-agent (arm64 + x86_64)", lipo.DeclaredBy);
        Assert.Contains("scripts/build-agent-helper.sh", lipo.DeclaredBy);
    }

    [Fact]
    public void A_go_mod_newer_than_the_contract_raises_the_minimum()
    {
        var root = HcgServerLayout();
        File.WriteAllText(Path.Combine(root, "go.mod"), "module x\n\ngo 1.26.1\n");
        var (contract, _) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        Assert.Equal("1.26.1", Assert.Single(BuildRequirementsInspector.FromContract(root, contract!), r => r.ToolId == "go").MinimumVersion);
    }

    [Fact]
    public void The_whole_project_check_finds_Swift_the_contract_and_the_Go_module()
    {
        var reqs = BuildRequirementsInspector.ForProject(HcgServerLayout(), null);
        Assert.Equal("5.9", Assert.Single(reqs, r => r.ToolId == "swift").MinimumVersion);
        Assert.Equal(0, SwiftCapabilities.Compare(Assert.Single(reqs, r => r.ToolId == "go").MinimumVersion!, "1.25.0"));
        Assert.Contains(reqs, r => r.ToolId == "lipo");
    }

    [Fact]
    public void Script_checks_are_read_without_running_the_script()
    {
        var root = HcgServerLayout();
        Assert.Equal(["go", "lipo"], BuildRequirementsInspector.ScriptChecks(Path.Combine(root, "scripts", "build-agent-helper.sh")));
    }

    [Theory]
    [InlineData(">=18.17 <23", "18.17")]
    [InlineData("^20.11.0", "20.11.0")]
    [InlineData("~=3.10", "3.10")]
    [InlineData(">= 3.11", "3.11")]
    public void Version_specs_give_their_lowest_version(string spec, string expected) =>
        Assert.Equal(expected, BuildRequirementsInspector.MinimumFromSpec(spec));

    [Fact]
    public async Task The_build_reports_a_missing_contract_tool_with_the_way_to_Build_Readiness()
    {
        var root = HcgServerLayout();
        var (contract, _) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        var (tools, error) = await SwiftContractPlanner.ResolveToolsAsync(contract!, new ExecutableLocator(_ => "", _ => false),
            (_, _) => Task.FromResult((0, "")), new Dictionary<string, string>(), CancellationToken.None,
            (name, min, _) => Task.FromResult(new ToolResolution(ReadinessState.NotFound, null, [], "Go was not found on PATH or in /opt/homebrew/bin.")));
        Assert.Empty(tools);
        Assert.Contains("go >= 1.25", error);
        Assert.Contains("Build Readiness", error);
        Assert.Contains(SwiftContractPlanner.ReadinessCode, error);
    }

    [Fact]
    public async Task The_build_uses_the_installation_Build_Readiness_resolves()
    {
        var root = HcgServerLayout();
        var (contract, _) = SwiftPackagingContractReader.Read(root, "macos/HCGServerApp");
        var asked = new List<(string, string?)>();
        var (tools, error) = await SwiftContractPlanner.ResolveToolsAsync(contract!, new ExecutableLocator(_ => "", _ => false),
            (_, _) => Task.FromResult((0, "")), new Dictionary<string, string>(), CancellationToken.None,
            (name, min, _) => { asked.Add((name, min)); return Task.FromResult(new ToolResolution(ReadinessState.Ready, new ToolInstallation("/opt/homebrew/bin/go", "1.25.4", "Homebrew (Apple Silicon)"), [], "")); });
        Assert.Null(error);
        Assert.Equal(("go", "1.25"), Assert.Single(asked));
        Assert.Equal("/opt/homebrew/bin/go", Assert.Single(tools).Path);
    }

    // ---------- profiles, chosen locations, remote hosts ----------

    private static BuildReadinessService Service(FakeMac mac, ISettingsStore settings, params IBuildHost[] hosts) =>
        new(new ProbeRunner(mac), settings, new NoToolchains(), hosts, TimeProvider.System, mac.Host);

    private sealed class NoToolchains : IToolchainService
    {
        public ToolchainReport? Latest => null;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(Fixtures.Toolchains());
    }

    [Fact]
    public async Task A_location_chosen_for_one_project_goes_into_its_profile_and_leaves_other_projects_alone()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/go"] = "go version go1.26.0 darwin/arm64";
        mac.Files["/Volumes/Tools/go1.25/bin/go"] = "go version go1.25.4 darwin/arm64";
        var settings = new MemorySettingsStore();
        var svc = Service(mac, settings);
        var server = Guid.NewGuid();
        var other = Guid.NewGuid();

        var (check, saved) = await svc.ChooseLocationAsync("go", "/Volumes/Tools/go1.25", "1.25", new ToolPathScope.Project(server, "HCG Server"));
        Assert.True(saved, check.Detail);
        var s = await settings.LoadAsync();
        var profile = Assert.Single(s.ToolchainProfiles!);
        Assert.Equal("HCG Server", profile.Name);
        Assert.Equal("/Volumes/Tools/go1.25/bin/go", profile.ToolPaths["go"]);
        Assert.Equal(profile.Id, s.ProjectToolchainProfiles![server.ToString()]);
        Assert.Null(s.ToolPaths);

        Assert.Equal("/Volumes/Tools/go1.25/bin/go", (await svc.ResolveForBuildAsync("go", "1.25", server, CancellationToken.None)).Selected!.Path);
        Assert.Equal("/opt/homebrew/bin/go", (await svc.ResolveForBuildAsync("go", "1.25", other, CancellationToken.None)).Selected!.Path);

        // The profile is reusable: another project can use it too, and leaving it restores automatic detection.
        await svc.AssignProfileAsync(other, profile.Id);
        Assert.Equal("/Volumes/Tools/go1.25/bin/go", (await svc.ResolveForBuildAsync("go", null, other, CancellationToken.None)).Selected!.Path);
        await svc.AssignProfileAsync(other, null);
        Assert.Equal("/opt/homebrew/bin/go", (await svc.ResolveForBuildAsync("go", null, other, CancellationToken.None)).Selected!.Path);
    }

    [Fact]
    public async Task A_location_that_does_not_work_is_not_saved()
    {
        var mac = new FakeMac();
        mac.Files["/Users/owner/old-go/bin/go"] = "go version go1.21.0 darwin/arm64";
        var settings = new MemorySettingsStore();
        var (check, saved) = await Service(mac, settings).ChooseLocationAsync("go", "/Users/owner/old-go/bin/go", "1.25", new ToolPathScope.AllProjects());
        Assert.False(saved);
        Assert.Equal(ReadinessState.IncompatibleVersion, check.State);
        Assert.Null((await settings.LoadAsync()).ToolPaths);
    }

    [Fact]
    public async Task The_readiness_report_for_HCG_Server_lists_install_options_and_blocks_the_build_when_Go_is_missing()
    {
        var mac = new FakeMac();
        mac.Files["/opt/homebrew/bin/brew"] = "";
        mac.Files["/usr/bin/lipo"] = "";
        mac.Files["/usr/bin/swift"] = "Apple Swift version 6.1.2";
        var report = await Service(mac, new MemorySettingsStore()).CheckAsync("HCG Server", HcgServerLayout(), Guid.NewGuid(), null, null);
        Assert.False(report.CanBuild);
        var go = Assert.Single(report.Blockers);
        Assert.Equal("go", go.Requirement.ToolId);
        Assert.Equal(ReadinessState.NotFound, go.State);
        Assert.Contains(go.Install, o => o.Runnable && o.CommandLine == "/opt/homebrew/bin/brew install go");
        Assert.Equal("https://go.dev/dl/", go.OfficialUrl);
        Assert.Contains(report.Items, i => i.Requirement.ToolId == "lipo" && i.State == ReadinessState.Ready);
        Assert.Contains(report.Items, i => i.Requirement.ToolId == "swift" && i.State == ReadinessState.Ready);
    }

    private sealed class FakeHost(BuildHostKind kind, Dictionary<string, string> tools) : IBuildHost
    {
        public BuildHostInfo Describe() => new(kind == BuildHostKind.Local ? "local" : "mac-mini", kind, kind == BuildHostKind.Local ? "This computer" : "Studio Mac mini (HCG Server)", "macOS 15", "arm64", tools);
        public string? CannotBuild(TargetPlatform platform) => null;
    }

    [Fact]
    public async Task An_authorized_remote_build_host_with_a_compatible_Go_is_offered_without_sending_anything()
    {
        var mac = new FakeMac();
        mac.Files["/usr/bin/lipo"] = "";
        mac.Files["/usr/bin/swift"] = "Apple Swift version 6.1.2";
        var svc = Service(mac, new MemorySettingsStore(),
            new FakeHost(BuildHostKind.Local, new() { ["go"] = "go1.26.0" }),
            new FakeHost(BuildHostKind.HcgServerHost, new() { ["go"] = "go version go1.25.4 darwin/arm64" }));
        var report = await svc.CheckAsync("HCG Server", HcgServerLayout(), null, null, null);
        var go = Assert.Single(report.Blockers);
        var remote = Assert.Single(go.Remote); // never "This computer"
        Assert.Equal("mac-mini", remote.HostId);
        Assert.Contains("sends no source files", remote.Transfer);
    }
}
