using System.Collections.ObjectModel;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed class SigningCapabilityItem(SigningCapability c)
{
    public string Name => c.DisplayName;
    public string Platform => c.Platform.DisplayName();
    public string Description => c.Description;
    public string StatusText => c.StatusText;
    public string Tools => string.Join(", ", c.RequiredToolchainIds);
    public StatusTag Status => new("Not implemented", "neutral");
    public StatusTag ToolStatus => !c.HostSupported ? new StatusTag("Other host", "neutral") : c.ToolsAvailable ? new StatusTag("Tools found", "success") : new StatusTag("Tools missing", "warning");
}

public sealed class SigningViewModel(IToolchainService toolchains, IBuildWorker worker, AppleSigningViewModel apple) : PageViewModel
{
    public override string Title => "Signing & Security";
    public override string Subtitle => "Apple signing identities and profiles, project signing, trust boundaries and secret handling.";

    /// <summary>HCG Signing Vault: Apple signing on this Mac (and optional account metadata).</summary>
    public AppleSigningViewModel Apple => apple;

    public ObservableCollection<SigningCapabilityItem> Capabilities { get; } = [];
    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;
    public string TrustExplanation => TrustPolicy.TrustBoundaryExplanation;
    public string SecretsStatement => TrustPolicy.SecretsStatement;
    public string IsolationDescription => worker.Description;
    public string BlockedPrefixes => string.Join("  ", BuildEnvironmentPolicy.BlockedPrefixes.Select(p => p + "*"));
    public string BlockedFragments => string.Join("  ", BuildEnvironmentPolicy.SecretNameFragments.Select(p => "*" + p + "*"));
    public string SignatureStatement =>
        "Builds are signed only with identities that are in this Mac's keychain, for the route you choose. Signing, notarization, uploading and publishing " +
        "are separate steps; HCG never notarizes, uploads or publishes on its own. HCG never invents certificates, never stores private keys or passwords " +
        "outside the keychain, and never revokes certificates.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var report = await toolchains.GetAsync();
        Capabilities.Clear();
        foreach (var c in SigningCapabilityCatalog.Evaluate(report)) Capabilities.Add(new SigningCapabilityItem(c));
        await apple.LoadAsync();
    }
}
