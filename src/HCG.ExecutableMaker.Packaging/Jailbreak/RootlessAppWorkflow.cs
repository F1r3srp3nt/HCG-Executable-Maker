using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// The one workflow behind the UI and the command line: Inspect the app a build produced → Package it as a rootless
/// .deb → Validate the .deb → Export the HCG Repo candidate handoff. Each stage reports what it did; the first failing
/// stage stops the workflow and says why. Nothing is installed, uploaded or published.
/// </summary>
public sealed class RootlessAppWorkflow(IProcessRunner runner, TimeProvider time)
{
    public enum Stage { Inspect, Package, Validate, Handoff, Done }

    public sealed record Request(
        string Source,
        string OutputDir,
        RootlessAppPackageSpec Spec,
        PackageProvenance Provenance,
        CandidateTexts Texts,
        string? LdidPath,
        string? DeviceIosVersion = null,
        IReadOnlyList<(string Version, string Sha256)>? PublishedVersions = null,
        bool ExportHandoff = true);

    public sealed record Outcome(
        Stage ReachedStage,
        bool Succeeded,
        string? Failure,
        IosAppBundle? App,
        RootlessAppPackager.Result? Package,
        RootlessAppDebValidator.Result? Validation,
        CandidateHandoff.Output? Handoff,
        IReadOnlyList<string> Log);

    public async Task<Outcome> RunAsync(Request r, Action<Stage, string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Log(Stage s, string m) { log.Add($"[{s}] {m}"); progress?.Invoke(s, m); }
        IosAppBundle? app = null;
        RootlessAppPackager.Result? pkg = null;
        RootlessAppDebValidator.Result? val = null;
        Directory.CreateDirectory(r.OutputDir);

        // Inspect.
        try
        {
            Log(Stage.Inspect, $"Reading {r.Source}");
            app = IosAppBundle.Open(r.Source, r.OutputDir);
            Log(Stage.Inspect, $"{app.Name} · {app.BundleId} · {app.ShortVersion ?? "?"} ({app.BuildVersion ?? "?"}) · iOS {app.MinimumOS ?? "?"}+ · from {app.SourceKind}");
            foreach (var (k, v) in app.BuildInfo) Log(Stage.Inspect, $"  {k}: {v}");
            foreach (var b in app.Binaries)
                Log(Stage.Inspect, $"  {b.RelativePath}: " + string.Join("; ", b.Slices.Select(s => $"{s.Architecture} {s.Platform ?? "?"} {s.MinimumOS ?? "?"}{(s.HasCodeSignature ? " signed" : " unsigned")}")));
            var problems = RootlessAppPackager.CheckSpec(r.Spec, app);
            if (problems.Count > 0)
            {
                foreach (var p in problems) Log(Stage.Inspect, "PROBLEM " + p);
                return new(Stage.Inspect, false, string.Join(" ", problems), app, null, null, null, log);
            }
            var unsigned = app.Binaries.Count(b => b.Slices.Any(s => !s.HasCodeSignature));
            if (unsigned > 0 && r.LdidPath is null)
            {
                const string missing = "Missing prerequisite: ldid. The build's Mach-O files are unsigned and must be pseudo-signed. Install ldid (macOS: brew install ldid).";
                Log(Stage.Inspect, missing);
                return new(Stage.Inspect, false, missing, app, null, null, null, log);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or InvalidCastException)
        {
            Log(Stage.Inspect, "FAILED " + ex.Message);
            return new(Stage.Inspect, false, ex.Message, app, null, null, null, log);
        }

        // Package.
        try
        {
            pkg = await RootlessAppPackager.PackageAsync(app, r.Spec, r.OutputDir, r.LdidPath, runner, m => Log(Stage.Package, m), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Log(Stage.Package, "FAILED " + ex.Message);
            return new(Stage.Package, false, ex.Message, app, null, null, null, log);
        }

        // Validate.
        val = await RootlessAppDebValidator.ValidateAsync(pkg.DebPath,
            new RootlessAppExpectation(r.Spec.PackageId, r.Spec.Version, app.BundleId, r.DeviceIosVersion, r.PublishedVersions), ct).ConfigureAwait(false);
        foreach (var c in val.Checks) Log(Stage.Validate, $"[{c.Status}] {c.Name}: {c.Detail}");
        await File.WriteAllTextAsync(Path.Combine(r.OutputDir, "VALIDATION.txt"), val.Report, ct).ConfigureAwait(false);
        if (!val.Passed)
            return new(Stage.Validate, false, "Validation failed: " + string.Join("; ", val.Checks.Where(c => c.Status == VerificationStatus.Failed).Select(c => c.Name)), app, pkg, val, null, log);

        if (!r.ExportHandoff) return new(Stage.Done, true, null, app, pkg, val, null, log);

        // Handoff.
        var folder = Path.Combine(r.OutputDir, $"{r.Spec.PackageId}_{r.Spec.Version.Replace(':', '_')}-candidate-handoff");
        var handoff = await CandidateHandoff.ExportAsync(folder, pkg.DebPath, app, r.Spec, val, r.Provenance, r.Texts, pkg.SignedFiles, time.GetUtcNow(), ct).ConfigureAwait(false);
        Log(Stage.Handoff, $"Exported {handoff.Files.Count} files to {folder}");
        foreach (var m in handoff.Missing) Log(Stage.Handoff, "MISSING " + m);
        return new(Stage.Done, true, null, app, pkg, val, handoff, log);
    }
}
