using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --mac-distribute …</c>: Developer ID distribution of a macOS app that was built elsewhere,
/// without rebuilding it (the same engine as the Sign &amp; Notarize page). Read-only inspection, signing, notarization
/// (only with <c>--notarize</c>, which sends the files to Apple), packaging and Release Passports. Nothing is uploaded to
/// HCG Store.
/// </summary>
internal static class MacDistributeCommand
{
    public const string Usage = """
        Usage:
          --mac-distribute --list-identities
          --mac-distribute --inspect --input <App.app|app.zip> [--checksums SHA256SUMS.txt]
          --mac-distribute --input <App.app|app.zip> [--checksums SHA256SUMS.txt] [--origin "CI run …"]
              --release-version 1.0.0-beta.2 [--build-number 5] [--bundle-id id] --arch arm64|x64|universal
              --identity <Developer ID Application SHA-1> [--installer <Developer ID Installer SHA-1>]
              (--entitlement <key> … | --entitlements-file <file.entitlements> | --no-entitlements) --confirm-entitlements
              [--formats zip,pkg,dmg] --out <folder> [--name <file base name>]
              [--notarize --notary-profile <keychain profile>]      sends the app, PKG and DMG to Apple's notary service
              [--source-repo <checkout> [--expected-commit <sha>]] Release Passports (after --notarize)
        """;

    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> All(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        using var services = Composition.Build(paths);
        var service = services.GetRequiredService<ExistingMacAppDistribution>();
        var ct = CancellationToken.None;
        Console.WriteLine(HostInfo.BuildIdentity);

        if (args.Contains("--list-identities"))
        {
            var inv = new MacSigningInventoryReader(services.GetRequiredService<IProcessRunner>(), TimeProvider.System).ReadAsync(ct).GetAwaiter().GetResult();
            if (inv.Problem is { } p) Console.WriteLine("Keychain: " + p);
            foreach (var i in inv.OfKind(AppleCertificateKind.DeveloperIdApplication, AppleCertificateKind.DeveloperIdInstaller))
                Console.WriteLine($"{i.Sha1}  {i.Name}  [{i.Kind}{(i.TeamId is { } t ? ", team " + t : "")}]");
            if (!inv.OfKind(AppleCertificateKind.DeveloperIdApplication).Any())
                Console.WriteLine("No Developer ID Application identity in the login keychain: import its .p12 in Signing & Security › Apple Signing.");
            return 0;
        }

        var input = Arg("--input");
        if (input is null) { Console.Error.WriteLine(Usage); return 2; }

        if (args.Contains("--inspect"))
        {
            var inspection = service.InspectAsync(input, Arg("--checksums"), ct).GetAwaiter().GetResult();
            var f = inspection.Facts;
            Console.WriteLine($"{f.Label}: {f.BundleIdentifier} {f.ShortVersion} ({f.BuildVersion}) · {string.Join("/", f.Architectures)} · macOS {f.MinimumSystemVersion ?? "?"}+");
            if (inspection.SignatureAsReceived is { } sig) Console.WriteLine("Signature as received: " + sig);
            Console.WriteLine($"Nested code ({inspection.NestedCode.Count}): {string.Join(", ", inspection.NestedCode)}");
            foreach (var c in inspection.Checks) Console.WriteLine($"[{c.Status}] {c.Name}: {c.Detail}");
            return inspection.Usable ? 0 : 6;
        }

        IReadOnlyList<string> entitlements = All("--entitlement").ToList();
        if (Arg("--entitlements-file") is { } ef)
        {
            var (keys, problems) = ExistingMacAppDistribution.ReadEntitlementsFile(ef);
            if (problems.Count > 0) { Console.Error.WriteLine(string.Join(Environment.NewLine, problems)); return 2; }
            entitlements = [.. entitlements, .. keys];
        }
        if (entitlements.Count == 0 && !args.Contains("--no-entitlements"))
        {
            Console.Error.WriteLine("Say which entitlements the app's signature carries: --entitlement <key> (repeatable), --entitlements-file, or --no-entitlements.");
            return 2;
        }
        var formats = (Arg("--formats") ?? "zip,pkg").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arch = (Arg("--arch") ?? "").ToLowerInvariant() switch { "arm64" => TargetArchitecture.Arm64, "x64" or "x86_64" => TargetArchitecture.X64, "universal" => TargetArchitecture.Universal, _ => (TargetArchitecture?)null };
        if (Arg("--release-version") is not { } version || Arg("--identity") is not { } identity || Arg("--out") is not { } output || arch is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var request = new ExistingMacAppRequest
        {
            Input = input, ChecksumsFile = Arg("--checksums"), Origin = Arg("--origin"), ReleaseVersion = version, ExpectedBuildNumber = Arg("--build-number"),
            ExpectedBundleIdentifier = Arg("--bundle-id"), Architecture = arch.Value, DeveloperIdApplicationSha1 = identity, InstallerSha1 = Arg("--installer"),
            Entitlements = entitlements.Distinct(StringComparer.Ordinal).ToList(), EntitlementsConfirmed = args.Contains("--confirm-entitlements"),
            Zip = formats.Contains("zip"), Pkg = formats.Contains("pkg"), Dmg = formats.Contains("dmg"), OutputDirectory = Path.GetFullPath(output), ArtifactBaseName = Arg("--name"),
        };

        BuildRecord build;
        try { build = service.SignAsync(request, Console.WriteLine, ct).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("Not signed: " + ex.Message); return 6; }
        Report(build);
        if (build.Status != BuildStatus.Succeeded) return 6;

        if (!args.Contains("--notarize"))
        {
            Console.WriteLine("Signed and verified. Not notarized: run again with --notarize --notary-profile <name> to send it to Apple, then the ZIP/PKG/DMG are made from the stapled app.");
            return 0;
        }
        var profile = Arg("--notary-profile");
        if (ExistingMacAppDistribution.NotarizeBlocker(build, profile) is { } blocker) { Console.Error.WriteLine(blocker); return 2; }
        var results = service.NotarizeAndPackageAsync(build, profile!, Console.WriteLine, ct).GetAwaiter().GetResult();
        foreach (var r in results) foreach (var s in r.Steps) Console.WriteLine($"[{s.Status}] {r.Artifact} · {s.Name}: {s.Detail}");
        Report(build);
        if (!ExistingMacAppDistribution.ReadyForDistribution(build)) { Console.Error.WriteLine("Not ready for distribution: see the failed steps above."); return 7; }

        if (Arg("--source-repo") is { } repo || args.Contains("--passports"))
        {
            try
            {
                foreach (var (file, passport, problems) in service.WritePassportsAsync(build, Arg("--source-repo"), Arg("--expected-commit"), ct).GetAwaiter().GetResult())
                    Console.WriteLine(passport is not null ? $"Release Passport: {passport}" : $"{Path.GetFileName(file)}: no passport ({string.Join("; ", problems)})");
            }
            catch (InvalidOperationException ex) { Console.Error.WriteLine("Passports not written: " + ex.Message); return 6; }
        }
        Console.WriteLine($"Ready for Ship to Store: {build.Settings.OutputDirectory}. Nothing was uploaded or published.");
        return 0;
    }

    private static void Report(BuildRecord build)
    {
        Console.WriteLine($"Job {build.Id} · {build.Status}{(build.FailureReason is { } f ? " · " + f : "")}");
        foreach (var a in build.Artifacts)
        {
            Console.WriteLine($"  {a.FileName} · {a.Verification} · SHA-256 {a.Sha256}");
            foreach (var c in a.Checks) Console.WriteLine($"    [{c.Status}] {c.Name}: {c.Detail}");
        }
    }
}
