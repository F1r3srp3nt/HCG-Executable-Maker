using HCG.ExecutableMaker.BuildEngine.Windows;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// A project's own Windows packaging contract (windows/WindowsPackaging.json), followed as declared:
/// <code>
/// HCGExecutableMaker --windows-contract &lt;source folder&gt;                       review: steps, tools and the contract's SHA-256
/// HCGExecutableMaker --windows-contract &lt;source folder&gt; --confirm &lt;sha256&gt; [--out DIR]
/// </code>
/// The steps run in a copy of the source folder (the original is never written to). Exit codes: 0 packaged and
/// verified, 2 usage or not confirmed, 3 no or invalid contract, 4 a required tool is missing, 6 a step or a verification
/// failed. Nothing is signed, uploaded or published; Ship to Store takes the files and passports separately.
/// </summary>
internal static class WindowsContractCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null; }
        var source = Arg("--windows-contract");
        if (source is null || !Directory.Exists(source)) { Console.Error.WriteLine("--windows-contract needs the project's source folder."); return 2; }
        source = Path.GetFullPath(source);
        var (contract, problem) = WindowsPackagingContract.Find(source);
        if (contract is null)
        {
            Console.Error.WriteLine(problem ?? $"No Windows packaging contract ({string.Join(" or ", WindowsPackagingContract.Locations)}) in {source}.");
            return 3;
        }
        foreach (var line in WindowsContractPackager.Review(contract)) Console.WriteLine(line);
        var confirm = Arg("--confirm");
        if (confirm is null)
        {
            Console.WriteLine();
            Console.WriteLine($"Nothing has run. To run these steps, confirm this exact file: --confirm {contract.Sha256}");
            return 2;
        }

        var outDir = Path.GetFullPath(Arg("--out") ?? Path.Combine(Environment.CurrentDirectory, $"{contract.Product.Replace(' ', '-')}-windows-{contract.Architecture}"));
        var work = Path.Combine(Path.GetTempPath(), "hcg-windows-contract-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine();
        Console.WriteLine($"Copying the source to {work} (the original is not written to)…");
        try
        {
            CopyTree(source, work);
            var copied = WindowsPackagingContract.Find(work).Contract ?? contract;
            var result = new WindowsContractPackager(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner())
                .RunAsync(new WindowsContractPackager.Request { SourceRoot = work, Contract = copied, ConfirmedSha256 = confirm, OutputDirectory = outDir }, Console.WriteLine, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine((result.Succeeded ? "SUCCEEDED: " : "FAILED: ") + result.Summary);
            if (result.Artifacts.Count > 0) Console.WriteLine("Output folder: " + outDir);
            if (result.Succeeded) return 0;
            return result.Readiness.Any(r => r.Status == VerificationStatus.Failed) ? 4 : result.Artifacts.Count == 0 && result.Summary.StartsWith("Not run", StringComparison.Ordinal) ? 2 : 6;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A plain copy, Git metadata included (the contract's scripts read the commit); symbolic links are copied as links' targets are not followed out of the tree.</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) continue;
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!Directory.Exists(Path.GetDirectoryName(target))) continue;   // under a skipped link
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }
}
