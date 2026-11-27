namespace HCG.ExecutableMaker.Core;

/// <summary>
/// Which headless command an <c>HCGExecutableMaker</c> command line names. A command's own options may share a name with
/// Maker's options: <c>--package-windows … --version 1.2.0</c> is the product's version, not a request for Maker's. HCG
/// Mail's prerelease run hit exactly that: Maker printed its own version, exited 0 and packaged nothing. So the commands
/// are matched first, and <c>--version</c> means Maker's version only when no command is named.
/// </summary>
public static class CommandLine
{
    public const string MakerVersion = "--version";

    /// <summary>The headless commands, in the order they are matched.</summary>
    public static readonly IReadOnlyList<string> Commands =
    [
        "--toolchain-report", "--ship-to-store", "--release-passport", "--ship-to-repo", "--rootless-deb", "--rootless-deb-validate", "--xcode-mac",
        "--package-windows", "--windows-signing-doctor", "--authenticode-sign", "--mac-signing-doctor", "--impactor", "--hcg-session",
    ];

    /// <summary>The command <paramref name="args"/> names; <see cref="MakerVersion"/> when only <c>--version</c> is given; null for the UI.</summary>
    public static string? CommandOf(IReadOnlyList<string> args)
    {
        foreach (var c in Commands)
            if (args.Contains(c)) return c;
        return args.Contains(MakerVersion) ? MakerVersion : null;
    }
}
