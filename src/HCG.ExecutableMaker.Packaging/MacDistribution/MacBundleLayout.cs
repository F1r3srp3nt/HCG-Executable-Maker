using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// What distribution signing needs to know about the layout of a built .app: which code to sign and in which order,
/// whether data sits where only code belongs, and whether it is a .NET app (whose runtime needs just-in-time code).
/// </summary>
public static class MacBundleLayout
{
    /// <summary>The hardened-runtime evidence every .NET app carries: CoreCLR compiles code just in time.</summary>
    public static readonly EntitlementEvidence DotNetRuntime = new(
        "Just-in-time compilation (.NET runtime)", EntitlementEvidenceKind.RequiresEntitlement, null, "com.apple.security.cs.allow-jit",
        ".NET runtime (CoreCLR)", "CoreCLR compiles the app's code at run time into MAP_JIT memory; under the hardened runtime the app stops at start-up without this entitlement.");

    /// <summary>
    /// Code to sign before the bundle itself, inside out: every Mach-O file except the main executable, and every nested
    /// bundle (framework, helper app, plug-in, XPC service), deepest first, so each outer seal covers the inner signatures.
    /// </summary>
    public static IReadOnlyList<string> SigningOrder(string app, MacAppFacts facts)
    {
        var main = facts.Executable is { } e ? $"Contents/MacOS/{e}" : null;
        var items = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in facts.MachOFiles.Keys)
        {
            if (rel == main) continue;
            items.Add(rel);
            // Every enclosing nested bundle below Contents is signed after its contents.
            var parts = rel.Split('/');
            for (var i = 1; i < parts.Length - 1; i++)
                if (IsBundleDirectory(parts[i])) items.Add(string.Join('/', parts.Take(i + 1)));
        }
        return items.OrderByDescending(r => r.Count(c => c == '/')).ThenBy(r => r, StringComparer.Ordinal)
            .Select(r => Path.Combine(app, r.Replace('/', Path.DirectorySeparatorChar))).ToList();
    }

    private static bool IsBundleDirectory(string name) =>
        name.EndsWith(".framework", StringComparison.Ordinal) || name.EndsWith(".app", StringComparison.Ordinal) || name.EndsWith(".appex", StringComparison.Ordinal)
        || name.EndsWith(".xpc", StringComparison.Ordinal) || name.EndsWith(".bundle", StringComparison.Ordinal) || name.EndsWith(".plugin", StringComparison.Ordinal);

    /// <summary>Files in Contents/MacOS that are not Mach-O code (a data file there is signed only in extended attributes, which ZIPs and installers drop).</summary>
    public static IReadOnlyList<string> DataInMacOS(string app, MacAppFacts facts)
    {
        var dir = Path.Combine(app, "Contents", "MacOS");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).LinkTarget is null)
            .Select(f => Path.GetRelativePath(app, f).Replace('\\', '/'))
            .Where(rel => !facts.MachOFiles.ContainsKey(rel))
            .Order(StringComparer.Ordinal).ToList();
    }

    public const string DataInMacOSAdvice =
        "A data file there can be signed only in extended attributes, which ZIPs and installers drop, and macOS then reports the downloaded app as damaged. " +
        "Publish the .NET app as a single file (PublishSingleFile, IncludeNativeLibrariesForSelfExtract=false) or move data into Contents/Resources.";

    /// <summary>Whether the app carries the .NET runtime (CoreCLR in the single-file host, or libcoreclr beside it).</summary>
    public static bool LooksLikeDotNet(string app, MacAppFacts facts)
    {
        if (facts.MachOFiles.Keys.Any(k => k.EndsWith("/libcoreclr.dylib", StringComparison.Ordinal) || k.EndsWith("/libclrjit.dylib", StringComparison.Ordinal))) return true;
        if (facts.Executable is not { } exe) return false;
        var path = Path.Combine(app, "Contents", "MacOS", exe);
        return File.Exists(path) && Contains(path, "System.Private.CoreLib"u8.ToArray());
    }

    private static bool Contains(string file, byte[] needle)
    {
        using var s = File.OpenRead(file);
        var buffer = new byte[1 << 20];
        var carry = 0;
        int read;
        while ((read = s.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var span = buffer.AsSpan(0, carry + read);
            if (span.IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, span.Length);
            span[^carry..].CopyTo(buffer);
        }
        return false;
    }
}

/// <summary>
/// The macOS Developer ID route saved on the Signing &amp; Security page (Project signing) as the starting point of a
/// macOS build's distribution choices. Only references are used (identity SHA-1s); identities that are no longer in the
/// keychain are not applied, and the reason is shown.
/// </summary>
public static class SavedMacSigning
{
    /// <returns>The options to start from, and a note for the person (null when nothing was saved for this route).</returns>
    public static (MacDistributionOptions Options, string? Note) Apply(MacDistributionOptions current, ProjectSigningConfiguration? saved, MacSigningInventory inventory)
    {
        if (saved is not { Platform: SigningPlatform.MacOs, Route: SigningRoute.MacDeveloperId }) return (current, null);
        if (saved.Mode != ProjectSigningMode.Manual || saved.IdentitySha1 is null)
            return (current, "Project signing (macOS Developer ID) is saved without a chosen identity; choose the Developer ID Application certificate here.");
        if (inventory.Find(saved.IdentitySha1) is not { Kind: AppleCertificateKind.DeveloperIdApplication } app)
            return (current, $"Project signing names the identity {saved.IdentitySha1[..Math.Min(8, saved.IdentitySha1.Length)]}…, which is not a Developer ID Application identity in this keychain. It was not applied.");
        var installer = saved.InstallerIdentitySha1 is { } isha ? inventory.Find(isha) : null;
        var options = current with
        {
            SigningMode = MacSigningMode.DeveloperId,
            DeveloperIdApplicationSha1 = app.Sha1,
            PkgInstallerSha1 = installer is { Kind: AppleCertificateKind.DeveloperIdInstaller } ? installer.Sha1 : current.PkgInstallerSha1,
            PkgInstallerName = installer is { Kind: AppleCertificateKind.DeveloperIdInstaller } ? installer.Name : current.PkgInstallerName,
        };
        var note = $"From Project signing (macOS Developer ID): {app.Name}" +
                   (installer is { Kind: AppleCertificateKind.DeveloperIdInstaller } ? $"; installer {installer.Name}." :
                    saved.InstallerIdentitySha1 is null ? "." : "; the saved installer identity is not in this keychain, so it was not applied.");
        return (options, note);
    }
}
