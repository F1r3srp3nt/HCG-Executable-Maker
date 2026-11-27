using System.Security.Cryptography.X509Certificates;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor.Inspection;

namespace HCG.Impactor.Signing;

/// <summary>
/// Signing identities from this Mac's keychain (via Executable Maker's Signing Vault — certificates and whether their
/// private key is present; private keys are never read) and provisioning profiles from Xcode's profile folders, plus any
/// profile files the user adds for one signing.
/// </summary>
public sealed class SigningInventorySource(AppleSigningAssetService assets, X509Certificate2Collection? trustedRoots = null) : ISigningInventorySource
{
    public string? LastProblem { get; private set; }

    public async Task<SigningInventorySnapshot> ReadAsync(IReadOnlyList<string>? extraProfileFiles, CancellationToken cancellationToken)
    {
        var inventory = await assets.ReadAsync(null, null, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        if (inventory.KeychainProblem is { } kp) problems.Add(kp);
        var profiles = new List<ProfileEntry>();
        foreach (var p in inventory.Profiles.Where(p => p.Path is not null))
        {
            var read = IpaInspector.ReadProfile(p.Path!, trustedRoots);
            profiles.Add(new ProfileEntry(read.Profile ?? p, read.RawEntitlements, p.Path));
        }
        foreach (var file in extraProfileFiles ?? [])
        {
            var read = IpaInspector.ReadProfile(Path.GetFullPath(file), trustedRoots);
            if (read.Profile is { } prof)
            {
                profiles.RemoveAll(x => string.Equals(x.Info.Uuid, prof.Uuid, StringComparison.OrdinalIgnoreCase));
                profiles.Add(new ProfileEntry(prof with { Path = Path.GetFullPath(file) }, read.RawEntitlements, Path.GetFullPath(file)));
            }
            else problems.Add($"{Path.GetFileName(file)}: {read.Problem}");
        }
        foreach (var (path, problem) in inventory.UnreadableProfiles) problems.Add($"{Path.GetFileName(path)}: {problem}");
        LastProblem = problems.Count == 0 ? null : string.Join(" ", problems);
        return new SigningInventorySnapshot(inventory.Certificates, profiles);
    }
}
