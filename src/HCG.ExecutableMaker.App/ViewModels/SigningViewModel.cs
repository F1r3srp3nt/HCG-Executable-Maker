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

public sealed class SigningViewModel(IToolchainService toolchains, IBuildWorker worker) : PageViewModel
{
    public override string Title => "Signing & Security";
    public override string Subtitle => "Trust boundaries, secret handling and the code-signing roadmap.";

    public ObservableCollection<SigningCapabilityItem> Capabilities { get; } = [];
    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;
    public string TrustExplanation => TrustPolicy.TrustBoundaryExplanation;
    public string SecretsStatement => TrustPolicy.SecretsStatement;
    public string IsolationDescription => worker.Description;
    public string BlockedPrefixes => string.Join("  ", BuildEnvironmentPolicy.BlockedPrefixes.Select(p => p + "*"));
    public string BlockedFragments => string.Join("  ", BuildEnvironmentPolicy.SecretNameFragments.Select(p => "*" + p + "*"));
    public string SignatureStatement =>
        "Every artifact produced by M0.1 is unsigned. macOS app hosts carry only the ad-hoc signature applied by the .NET SDK, " +
        "which is not a Developer ID signature. Nothing is notarized. HCG never invents certificates or signing identities, " +
        "never stores private keys or passwords, and never uploads artifacts or credentials anywhere.";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var report = await toolchains.GetAsync();
        Capabilities.Clear();
        foreach (var c in SigningCapabilityCatalog.Evaluate(report)) Capabilities.Add(new SigningCapabilityItem(c));
    }
}
