using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.UiDriver;

/// <summary>Ship to Store page, headless: the files from --source are added as from the Artifacts page; detection,
/// signature check and the signed-out state are shown (no Store is contacted without a session).</summary>
internal static partial class Program
{
    private static async Task<int> ShipPageAsync(MainWindowViewModel main)
    {
        var files = _args["source"].Split(',').Select(Path.GetFullPath).ToArray();
        await main.NavigateAsync("ship", files);
        var page = (ShipToStoreViewModel)main.CurrentPage!;
        await WaitUntil(() => page.Files.Count == files.Length && page.Files.All(f => !f.State.StartsWith("Checking", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "file detection");
        foreach (var f in page.Files) Log($"File {f.FileName}: {f.Platform} · {f.Size} · {f.Signing} · {f.State}{(f.HasProblems ? " · " + f.Problems : "")}");
        Log($"Connection: [{page.Connection}] {page.ConnectionDetail}");
        Check(page.Files.All(f => f.Candidate.IsSupported), "Every sample file is detected as a supported platform artifact");
        Check(page.Connection == "Sign in required", "Without an HCG Connect session the page asks for sign-in and contacts no Store");
        Shot("ship-to-store");
        return Failures.Count == 0 ? 0 : 1;
    }
}
