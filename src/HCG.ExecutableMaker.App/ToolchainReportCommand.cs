using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>Command-line toolchain report (no UI): every row with status, path, source and the exact fix.</summary>
internal static class ToolchainReportCommand
{
    public static int Run()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        services.GetRequiredService<DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult();
        var report = services.GetRequiredService<IToolchainDetector>().DetectAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"{HostInfo.BuildIdentity}");
        Console.WriteLine($"Host: {report.Host.DisplayName} ({report.Host.OsDescription})");
        Console.WriteLine($"Environment: JAVA_HOME={Environment.GetEnvironmentVariable("JAVA_HOME") ?? "(unset)"} ANDROID_HOME={Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "(unset)"} DEVELOPER_DIR={Environment.GetEnvironmentVariable("DEVELOPER_DIR") ?? "(unset)"} PATH={Environment.GetEnvironmentVariable("PATH")}");
        foreach (var t in report.Toolchains.OrderBy(t => t.Category, StringComparer.Ordinal))
        {
            Console.WriteLine($"[{t.Category}] {t.DisplayName}: {t.Status}{(t.Version is null ? "" : " " + t.Version)}{(t.ExecutablePath is null ? "" : " — " + t.ExecutablePath)}{(t.Source is null ? "" : " (via " + t.Source + ")")}");
            if (!string.IsNullOrEmpty(t.Detail)) Console.WriteLine("    " + t.Detail);
            if (!t.IsAvailable && !string.IsNullOrEmpty(t.InstallHint) && t.Status != ToolchainStatus.UnsupportedOnHost) Console.WriteLine("    Fix: " + t.InstallHint);
        }
        if (report.Xcode is { } x) Console.WriteLine($"Xcode: {x.State} — {x.Diagnosis}{(x.CorrectiveAction.Length > 0 ? " Fix: " + x.CorrectiveAction : "")}");
        foreach (var c in report.Java?.Candidates ?? [])
            Console.WriteLine(c.IsUsable ? $"JDK candidate: OK Java {c.Version} {c.Home} ({c.Source})" : $"JDK candidate: REJECTED {c.Home} ({c.Source}): {c.Problem}");
        Console.WriteLine($"JDK selected: {report.Java?.Selected?.Home ?? "(none)"} ({report.Java?.Selected?.Source})");
        if (report.AndroidSdk is { } a)
        {
            Console.WriteLine($"Android SDK: {a.Root ?? "(none)"} ({a.Source}){(a.Problem is null ? "" : " — " + a.Problem)}");
            Console.WriteLine($"  platforms: {string.Join(", ", a.Platforms)}; build-tools: {string.Join(", ", a.BuildTools.Select(b => b.Version + (b.IsComplete ? "" : " (incomplete)")))}; selected: {a.SelectedBuildTools?.Version ?? "(none)"}; licences accepted: {a.LicensesAccepted}");
            foreach (var l in a.ExaminedLocations) Console.WriteLine("  looked in: " + l);
        }
        return 0;
    }
}
