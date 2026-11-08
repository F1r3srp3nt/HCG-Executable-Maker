using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;

namespace HCG.ExecutableMaker.Tests;

public class InspectionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private ProjectInspectionService Service() => new(
        new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System), new HCG.ExecutableMaker.ProjectInspection.Mobile.MobileProjectInspector(TimeProvider.System) }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    private const string AvaloniaCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <AssemblyName>Mailer</AssemblyName>
            <ApplicationIcon>Assets\app.ico</ApplicationIcon>
            <RuntimeIdentifiers>win-x64;osx-arm64</RuntimeIdentifiers>
            <Version>2.3.4</Version>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Avalonia" Version="12.1.4" />
            <PackageReference Include="Avalonia.Desktop" Version="12.1.4" />
            <ProjectReference Include="..\Core\Core.csproj" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public async Task Detects_avalonia_application_in_solution()
    {
        _t.Write("Mail.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "Core\Core.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        _t.Write("App/App.csproj", AvaloniaCsproj);
        _t.Write("App/Assets/app.ico", "ico");
        _t.Write("App/Program.cs", "class Program { [STAThread] public static void Main(string[] args) {} }");
        _t.Write("Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);

        Assert.Equal("dotnet", a.InspectorId);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal("App/App.csproj", a.PrimaryProjectPath);
        Assert.Equal("Mailer", a.ProjectName);
        Assert.Equal("C#", a.Language);
        Assert.Equal("Avalonia desktop application", a.ApplicationType);
        Assert.Contains("Avalonia UI 12.1.4", a.Framework);
        Assert.Equal(".NET 10.0", a.Runtime);
        Assert.Equal(["Mail.sln"], a.SolutionFiles);
        var app = a.Projects.Single(p => p.Name == "App");
        Assert.True(app.IsAvalonia);
        Assert.Equal(["win-x64", "osx-arm64"], app.RuntimeIdentifiers);
        Assert.Equal("App/Assets/app.ico", app.ApplicationIcon);
        Assert.Equal("2.3.4", app.Version);
        Assert.Equal(["App/Program.cs"], app.EntryPointFiles);
        Assert.Equal([TargetPlatform.Windows, TargetPlatform.MacOS, TargetPlatform.Linux], app.CompatiblePlatforms);
        Assert.Contains(PackagingFormat.MacAppBundle, a.RecommendedFormats);
        Assert.Equal("Class library", a.Projects.Single(p => p.Name == "Core").ApplicationType);
        Assert.Empty(a.MissingDependencies);
    }

    [Fact]
    public async Task Reads_slnx_solution()
    {
        _t.Write("App.slnx", """<Solution><Folder Name="/src/"><Project Path="src/App/App.csproj" /></Folder><Project Path="tools/Tool.csproj" /></Solution>""");
        _t.Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _t.Write("src/App/Program.cs", "Console.WriteLine(\"hi\");");
        var paths = SolutionParser.GetProjectPaths(_t.Combine("App.slnx"));
        Assert.Equal(["src/App/App.csproj", "tools/Tool.csproj"], paths);

        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("Console application", a.ApplicationType);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("not in the workspace") && d.Message.Contains("Tool.csproj"));
        Assert.Contains("src/App/Program.cs (top-level statements)", a.Projects[0].EntryPointFiles);
        Assert.DoesNotContain(PackagingFormat.MacAppBundle, a.RecommendedFormats);
    }

    [Fact]
    public async Task Reports_missing_sdk_for_newer_target_framework()
    {
        _t.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        Assert.Contains(".NET SDK 11.0 or later", a.MissingDependencies);
        Assert.False(a.RequiredToolchains.Single().IsSatisfied);
    }

    [Fact]
    public async Task Reports_missing_sdk_when_none_installed()
    {
        _t.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains(), CancellationToken.None);
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
    }

    [Theory]
    [InlineData("10.0.100", "latestFeature", new[] { "10.0.114" }, true)]
    [InlineData("10.0.100", null, new[] { "10.0.114" }, true)]        // patch: same feature band 1xx
    [InlineData("10.0.200", null, new[] { "10.0.114" }, false)]
    [InlineData("9.0.100", "latestMajor", new[] { "10.0.114" }, true)]
    [InlineData("9.0.100", "latestFeature", new[] { "10.0.114" }, false)]
    [InlineData("10.0.114", "disable", new[] { "10.0.114" }, true)]
    public void GlobalJson_rollforward(string version, string? rollForward, string[] installed, bool expected) =>
        Assert.Equal(expected, new GlobalJson(version, rollForward, false).IsSatisfiedBy(installed));

    [Fact]
    public async Task Reports_global_json_mismatch()
    {
        _t.Write("global.json", """{ "sdk": { "version": "8.0.100" } }""");
        _t.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("8.0.100", a.GlobalJsonSdkVersion);
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        Assert.Contains(a.MissingDependencies, m => m.Contains("global.json"));
    }

    [Fact]
    public void Parses_multi_targeting_props_and_central_versions()
    {
        _t.Write("Directory.Build.props", """<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks><Company>Acme</Company></PropertyGroup></Project>""");
        _t.Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Serilog" Version="4.0.0" /></ItemGroup></Project>""");
        var proj = _t.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><Product>$(Company) Tool</Product></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)'=='Release'"><Optimize>true</Optimize><Product>ignored</Product></PropertyGroup>
              <ItemGroup><PackageReference Include="Serilog" /></ItemGroup>
            </Project>
            """);
        var file = MsBuildProjectFile.Parse(proj, [_t.Combine("Directory.Build.props")], MsBuildProjectFile.ReadCentralVersions(_t.Combine("Directory.Packages.props")));
        Assert.Equal("net8.0;net10.0", file.Get("TargetFrameworks"));
        Assert.Equal("Acme Tool", file.Get("Product"));
        Assert.Equal("true", file.Get("Optimize"));
        Assert.Equal("4.0.0", file.PackageReferences.Single().Version);
    }

    [Fact]
    public void Rejects_xml_external_entities()
    {
        var proj = _t.Write("Evil.csproj", """
            <?xml version="1.0"?>
            <!DOCTYPE Project [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Description>&xxe;</Description></PropertyGroup></Project>
            """);
        Assert.Throws<System.Xml.XmlException>(() => MsBuildProjectFile.Parse(proj));
    }

    [Theory]
    [InlineData(new[] { "net10.0" }, false, new[] { TargetPlatform.Windows, TargetPlatform.MacOS, TargetPlatform.Linux })]
    [InlineData(new[] { "net10.0-windows" }, true, new[] { TargetPlatform.Windows })]
    [InlineData(new[] { "net10.0" }, true, new[] { TargetPlatform.Windows })]
    [InlineData(new[] { "net48" }, false, new TargetPlatform[0])]
    [InlineData(new[] { "net10.0-android" }, false, new TargetPlatform[0])]
    [InlineData(new[] { "netstandard2.0" }, false, new TargetPlatform[0])]
    public void Computes_compatible_platforms(string[] tfms, bool windowsUi, TargetPlatform[] expected) =>
        Assert.Equal(expected, DotNetProjectInspector.ComputeCompatiblePlatforms(tfms, windowsUi, out _));

    [Fact]
    public async Task Wpf_is_windows_only()
    {
        _t.Write("W.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("WPF desktop application", a.ApplicationType);
        Assert.Equal([TargetPlatform.Windows], a.Projects[0].CompatiblePlatforms);
    }

    [Fact]
    public async Task Library_only_requires_configuration()
    {
        _t.Write("Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal(SupportLevel.RequiresConfiguration, a.Support);
        Assert.Null(a.PrimaryProjectPath);
    }

    [Fact]
    public async Task Test_projects_are_not_runnable()
    {
        _t.Write("T.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" /></ItemGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("Test project", a.Projects[0].ApplicationType);
        Assert.False(a.Projects[0].IsRunnableApplication);
    }

    [Theory]
    [InlineData("Cargo.toml", "[package]", "rust")]
    [InlineData("package.json", "{\"dependencies\":{\"react-native\":\"0.80\"}}", "react-native")]
    [InlineData("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0", "unity")]
    [InlineData("pyproject.toml", "[project]", "python")]
    [InlineData("CMakeLists.txt", "project(x)", "cpp")]
    public async Task Other_ecosystems_are_recognized_but_unsupported(string file, string content, string inspector)
    {
        _t.Write(file, content);
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal(inspector, a.InspectorId);
        Assert.Equal(SupportLevel.Unsupported, a.Support);
        Assert.Contains("does not build", a.SupportSummary);
    }

    [Fact]
    public async Task Empty_workspace_is_unrecognized()
    {
        _t.Write("readme.txt", "hello");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal(SupportLevel.Unrecognized, a.Support);
    }

    [Fact]
    public async Task Dotnet_wins_over_incidental_package_json()
    {
        _t.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _t.Write("package.json", "{}");
        var a = await Service().InspectAsync(_t.Path, Fixtures.Toolchains("10.0.114"), CancellationToken.None);
        Assert.Equal("dotnet", a.InspectorId);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("Node.js"));
    }
}
