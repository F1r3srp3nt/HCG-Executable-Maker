using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

public class CommandLineTests
{
    /// <summary>
    /// HCG Mail's prerelease run (HCG-Mail Actions run 45764398120): <c>--package-windows … --version 0.1.0-beta.1</c> printed
    /// Executable Maker's own version, exited 0 and packaged nothing. A command's <c>--version</c> is the product's.
    /// </summary>
    [Theory]
    [InlineData("--package-windows", "--identity", "app.json", "--version", "0.1.0-beta.1", "--targets", "win-x64")]
    [InlineData("--version", "1.2.0", "--package-windows", "--app-dir", "out")]
    [InlineData("--ship-to-store", "--version", "1.0.0")]
    [InlineData("--xcode-mac", "Project.zip", "--version", "2.0")]
    public void A_commands_own_version_option_never_triggers_Makers_version(params string[] args)
    {
        var command = CommandLine.CommandOf(args);
        Assert.NotNull(command);
        Assert.NotEqual(CommandLine.MakerVersion, command);
        Assert.Contains(command, args);
    }

    [Fact]
    public void Version_alone_identifies_Maker_and_nothing_starts_the_UI()
    {
        Assert.Equal(CommandLine.MakerVersion, CommandLine.CommandOf(["--version"]));
        Assert.Null(CommandLine.CommandOf([]));
        Assert.Null(CommandLine.CommandOf(["--page", "account"]));
    }

    /// <summary>Every command Program.cs routes is in <see cref="CommandLine.Commands"/>, so none can be shadowed by --version.</summary>
    [Fact]
    public void Every_routed_command_is_known_to_the_router()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "HCG.ExecutableMaker.App", "Program.cs"));
        var routed = Regex.Matches(program, "args\\.Contains\\(\"(--[a-z-]+)\"\\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(routed);
        Assert.All(routed, c => Assert.Contains(c, CommandLine.Commands));
        Assert.DoesNotContain("args.Contains(\"--version\")", program);
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "HCG.ExecutableMaker.App", "Program.cs"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
