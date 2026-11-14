using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>
/// Signing &amp; Security › Apple Signing as a user meets it: the identities and profiles on this computer, the import
/// choice (this Mac by default; account backup only when the HCG account service exists), the encrypted vault shown as
/// unavailable, and the project signing check for the imported project. Imports a profile when --pick-file is given.
/// </summary>
internal static partial class Program
{
    private static async Task<int> RunSigningAsync(MainWindowViewModel main)
    {
        await main.NavigateAsync("security", null);
        await Task.Delay(500);
        var page = (SigningViewModel)main.CurrentPage!;
        var apple = page.Apple;
        await WaitUntil(() => !apple.IsBusy, TimeSpan.FromMinutes(1), "keychain and profile detection");
        Log("Keychain: " + apple.KeychainStatus);
        Log("Account backup: " + apple.AccountStatus);
        Log("Vault: " + apple.VaultStatus);
        foreach (var c in apple.Certificates) Log($"Identity: {c.Name} · {c.Type} · {c.Team} · {c.Expiry} · [{c.ReadinessTag.Text}] · {c.KeyText} · {c.LocationText}");
        foreach (var p in apple.Profiles) Log($"Profile: {p.Name} · {p.Distribution} · {p.BundleId} · {p.Platforms} · {p.Expiry} · [{p.StateTag.Text}] · {p.Signature}");
        Check(!apple.BackUpToAccount, "Import goes to this Mac by default (account backup is opt-in)");
        Check(!apple.AccountAvailable, "Account backup is unavailable without HCG's signing-metadata service — not faked");
        Check(apple.VaultStatus.Contains("not available", StringComparison.Ordinal), "Encrypted private-key backup is shown as not available");
        Shot("signing-assets");

        if (Arg("pick-file") is { } file)
        {
            await apple.ImportProfileCommand.ExecuteAsync(null);
            Log("Profile import: " + apple.LastResult);
            Check(apple.LastResult is not null, "Profile import reports its outcome");
        }

        apple.SelectedProject = apple.SigningProjects.FirstOrDefault();
        await Task.Delay(300);
        Check(apple.SelectedProject is not null, "The imported project can be configured for signing");
        apple.SelectedMode = apple.Modes.First(m => m.Value == ProjectSigningMode.Manual);
        apple.SelectedRoute = apple.Routes.FirstOrDefault(r => r.Value == SigningRoute.IosAppStore) ?? apple.Routes.First();
        if (string.IsNullOrEmpty(apple.TeamId)) apple.TeamId = "ABCDE12345";
        Log("Route: " + apple.RouteDescription);
        await apple.ValidateCommand.ExecuteAsync(null);
        Log("Check: " + apple.ConfigStatus);
        foreach (var c in apple.Checks) Log($"Signing check [{c.Status.Text}] {c.Title}: {c.Detail} {(c.HasRemedy ? c.Remedy : "")} ({c.Scope})");
        Check(apple.Checks.Count > 0, "Signing checks are listed");
        Check(apple.Checks.Any(c => c.Scope.StartsWith("Needs Apple", StringComparison.Ordinal)), "Checks that need Apple are labelled as not checked by HCG");
        Check(apple.Checks.All(c => !c.Detail.Contains("toolchain missing", StringComparison.OrdinalIgnoreCase)), "Missing signing assets are not called a missing toolchain");
        Check(apple.Checks.All(c => !(c.Status.Text == "Passed" && c.Detail.Contains("not revoked", StringComparison.OrdinalIgnoreCase))), "No certificate is called 'not revoked'");
        ShotScrolledToEnd("signing-project-check");
        return 0;
    }
}
