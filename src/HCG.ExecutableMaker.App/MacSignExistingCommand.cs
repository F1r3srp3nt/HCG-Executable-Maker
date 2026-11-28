using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --mac-sign-existing --app &lt;X.app | X.zip&gt; [--input-checksums &lt;SHA256SUMS&gt;] --out &lt;dir&gt;
/// (--identity &lt;Developer ID Application SHA-1 or name&gt; | --test)
/// [--entitlements &lt;plist&gt;] --confirm-entitlements [--pkg [--installer-identity &lt;SHA-1 or name&gt;]] [--dmg]
/// [--notary-profile &lt;keychain profile&gt;] [--version 1.0.0-beta.2] [--build-number N] [--source-repo &lt;dir&gt;]
/// [--provider executable-maker-local|github-actions]</c>
/// <para>
/// The Sign an existing app page without the UI, for the same service: the input .app is copied (never changed), signed
/// inside out, verified, optionally notarized and stapled, zipped, optionally packaged as a .pkg, and described by a
/// Release Passport per file. <c>--test</c> makes an ad-hoc test build (no certificate, never notarized, named
/// …-adhoc-TEST-not-for-distribution). Nothing is published. The files listed as ready for Ship to Store are only those
/// that are notarized, stapled and accepted by Gatekeeper; a test build never is.
/// </para>
/// </summary>
internal static class MacSignExistingCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var app = Arg("--app");
        var output = Arg("--out");
        var test = args.Contains("--test");
        if (app is null || output is null || (Arg("--identity") is null && !test))
        {
            Console.Error.WriteLine("Usage: --mac-sign-existing --app <X.app | X.zip> [--input-checksums <SHA256SUMS>] --out <dir> (--identity <Developer ID Application SHA-1 or name> | --test) " +
                                    "[--entitlements <plist>] --confirm-entitlements [--pkg [--installer-identity <SHA-1 or name>]] [--dmg] [--notary-profile <name>] " +
                                    "[--version <release version>] [--build-number N] [--source-repo <dir>] [--provider <name>]");
            return 2;
        }
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("Signing an existing app runs on a Mac (codesign, ditto, pkgbuild, notarytool). Nothing was changed."); return 4; }
        Console.WriteLine(HostInfo.BuildIdentity);
        var runner = new ProcessRunner();
        var inventory = test && Arg("--installer-identity") is null
            ? new MacSigningInventory()
            : new MacSigningInventoryReader(runner, TimeProvider.System).ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        string? Resolve(string? wanted, AppleCertificateKind kind)
        {
            if (wanted is null) return null;
            var matches = inventory.Identities.Where(i => string.Equals(i.Sha1, wanted, StringComparison.OrdinalIgnoreCase) || i.Name == wanted).ToList();
            if (matches.Count == 1) return matches[0].Sha1;
            Console.Error.WriteLine(matches.Count == 0 ? $"No valid identity \"{wanted}\" in the keychain." : $"\"{wanted}\" matches {matches.Count} identities ({kind}); pass its SHA-1.");
            return wanted; // the plan reports it precisely
        }
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var request = new ExistingAppRequest
        {
            InputApp = Path.GetFullPath(app),
            InputChecksums = Arg("--input-checksums") is { } sums ? Path.GetFullPath(sums) : null,
            OutputDirectory = Path.GetFullPath(output),
            IdentitySha1 = test ? null : Resolve(Arg("--identity"), AppleCertificateKind.DeveloperIdApplication),
            AdHocTest = test,
            EntitlementsFile = Arg("--entitlements") is { } e ? Path.GetFullPath(e) : null,
            EntitlementsConfirmed = args.Contains("--confirm-entitlements"),
            MakePkg = args.Contains("--pkg"),
            MakeDmg = args.Contains("--dmg"),
            InstallerSha1 = Resolve(Arg("--installer-identity"), AppleCertificateKind.DeveloperIdInstaller),
            NotaryKeychainProfile = Arg("--notary-profile"),
            ReleaseVersion = Arg("--version"),
            BuildNumber = Arg("--build-number"),
            SourceRepository = Arg("--source-repo"),
            Provider = Arg("--provider") ?? "executable-maker-local",
        };
        var result = new ExistingAppDistribution(runner, paths.LogsDirectory)
            .RunAsync(request, inventory, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine();
        foreach (var f in result.Files) Console.WriteLine("File: " + f);
        foreach (var p in result.Passports) Console.WriteLine("Release Passport: " + p);
        if (result.Report is { } r) Console.WriteLine("Report: " + r);
        foreach (var f in result.ReadyForStore) Console.WriteLine("Ready for Ship to Store (private draft): " + f);
        if (result.StoreNote is { } note) Console.WriteLine(note);
        if (test) Console.WriteLine("TEST BUILD: ad-hoc signed and not notarized. Do not distribute these files.");
        Console.WriteLine(result.Succeeded ? "All steps passed." : $"{result.Steps.Count(s => s.Status == VerificationStatus.Failed)} step(s) FAILED.");
        return result.Succeeded ? 0 : 1;
    }
}
