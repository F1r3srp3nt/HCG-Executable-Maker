using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Tests;

/// <summary>A temporary directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hcg-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Zips
{
    /// <summary>Builds a ZIP from raw entry definitions (allows malicious names and attributes).</summary>
    public static string Create(string path, params (string Name, string Content, int? UnixMode)[] entries)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content, mode) in entries)
        {
            var e = zip.CreateEntry(name);
            if (mode is { } m) e.ExternalAttributes = unchecked(m << 16);
            if (!name.EndsWith('/'))
            {
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        }
        return path;
    }
}

public static class Fixtures
{
    /// <summary>A fully-qualified (but nonexistent) dotnet path valid on the current OS.</summary>
    public static string FakeDotNet => OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : "/usr/bin/dotnet";

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HCG.ExecutableMaker.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static string HelloAvalonia => Path.Combine(RepoRoot, "tests", "Fixtures", "HelloAvalonia");

    public static ToolchainReport Toolchains(params string[] sdkVersions) => new(
        HostInfo.Detect(),
        sdkVersions.Length == 0
            ? [new ToolchainInfo { Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build", Status = ToolchainStatus.Missing }]
            : [new ToolchainInfo { Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build", Status = ToolchainStatus.Installed, Version = sdkVersions[0], ExecutablePath = FakeDotNet, AdditionalVersions = sdkVersions }],
        null, null, DateTimeOffset.UtcNow);

    public static string? FindDotNet() => new ExecutableLocator().Find("dotnet", ExecutableLocator.DotNetCandidates());
}

/// <summary>Skips when no .NET SDK host is available (integration tests).</summary>
public sealed class RequiresDotNetFactAttribute : FactAttribute
{
    public RequiresDotNetFactAttribute()
    {
        if (Fixtures.FindDotNet() is null) Skip = "No .NET SDK found on this host.";
        else if (Environment.GetEnvironmentVariable("HCG_SKIP_INTEGRATION") == "1") Skip = "HCG_SKIP_INTEGRATION=1";
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix host (symlinks / file modes / sleep).";
    }
}

/// <summary>Skips when the JDK tools (keytool, jarsigner) are not available.</summary>
public sealed class RequiresJdkFactAttribute : FactAttribute
{
    public RequiresJdkFactAttribute()
    {
        if (JdkTools.Find("keytool") is null || JdkTools.Find("jarsigner") is null) Skip = "JDK keytool/jarsigner not found (JAVA_HOME or PATH).";
    }
}

public static class JdkTools
{
    public static string? Find(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home) dirs.Add(System.IO.Path.Combine(home, "bin"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => System.IO.Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    public static void Run(string tool, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Find(tool)!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException(tool + " timed out"); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} failed ({p.ExitCode}): {stderr} {stdout.Result}");
    }
}
