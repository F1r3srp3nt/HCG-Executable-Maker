using System.Text;
using System.Text.Json;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine.Doctor;

/// <summary>
/// Applies Build Doctor repairs, only ever after the user approved the exact change shown by <see cref="Preview"/>:
/// file edits are backed up under <c>&lt;workspace&gt;/doctor/backups/&lt;id&gt;/</c> with a manifest and can be rolled
/// back; commands (restore, workload install) run with the scrubbed build environment and their output is kept.
/// Nothing here downloads binaries of its own, installs silently or edits files outside the workspace source.
/// </summary>
public sealed class DoctorRepairs(IProcessRunner runner, TimeProvider time)
{
    private const string ManifestName = "repair.json";

    /// <summary>A readable diff of every file the repair would change (unified-style, changed lines with context).</summary>
    public static string Preview(DoctorRepair repair)
    {
        if (repair.Changes is not { Count: > 0 } changes)
            return repair.Command is { } c ? $"Command (runs only after you approve):\n  {c}\n\n{repair.Description}" : repair.Description;
        var sb = new StringBuilder();
        foreach (var ch in changes)
        {
            sb.AppendLine($"--- {ch.Path} (current)").AppendLine($"+++ {ch.Path} (after repair)");
            var a = ch.Before.Replace("\r", "").Split('\n');
            var b = ch.After.Replace("\r", "").Split('\n');
            var n = Math.Max(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                var x = i < a.Length ? a[i] : null;
                var y = i < b.Length ? b[i] : null;
                if (x == y) continue;
                for (var k = Math.Max(0, i - 2); k < i; k++) sb.AppendLine("  " + a[k]);
                if (x is not null) sb.AppendLine("- " + x);
                if (y is not null) sb.AppendLine("+ " + y);
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes the changed files after backing up the current ones. Refuses a change whose file no longer matches what was reviewed.</summary>
    public async Task<DoctorRepairRecord> ApplyFileChangesAsync(ProjectRecord project, DoctorRepair repair, CancellationToken ct)
    {
        if (repair.Kind != RepairKind.EditFiles || repair.Changes is not { Count: > 0 } changes)
            throw new InvalidOperationException("This repair does not change files.");
        var source = Path.GetFullPath(project.SourceDirectory);
        var stamp = time.GetUtcNow();
        var backup = Path.Combine(project.WorkspacePath, "doctor", "backups", $"{stamp:yyyyMMdd-HHmmss}-{Slug(repair.Id)}");
        foreach (var ch in changes)
        {
            var full = Path.GetFullPath(Path.Combine(source, ch.Path));
            if (!BuildDoctor.IsWithin(source, full)) throw new InvalidOperationException($"{ch.Path} is outside the workspace source; HCG does not change it.");
            var current = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
            if (current != ch.Before) throw new InvalidOperationException($"{ch.Path} changed since the repair was proposed. Run Build Doctor again.");
        }
        Directory.CreateDirectory(backup);
        foreach (var ch in changes)
        {
            var dest = Path.Combine(backup, "files", ch.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(source, ch.Path), dest, overwrite: false);
        }
        var record = new DoctorRepairRecord(repair.Id, repair.Title, stamp, backup, changes.Select(c => c.Path).ToList(), true, "Applied");
        await File.WriteAllTextAsync(Path.Combine(backup, ManifestName), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        foreach (var ch in changes)
            await File.WriteAllTextAsync(Path.Combine(source, ch.Path), ch.After, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>Restores the files a repair changed from its backup.</summary>
    public static async Task RollbackAsync(ProjectRecord project, DoctorRepairRecord record, CancellationToken ct)
    {
        if (record.BackupFolder is not { } backup || !Directory.Exists(backup)) throw new InvalidOperationException("The backup of this repair no longer exists.");
        var source = Path.GetFullPath(project.SourceDirectory);
        foreach (var rel in record.ChangedFiles)
        {
            var dest = Path.GetFullPath(Path.Combine(source, rel));
            if (!BuildDoctor.IsWithin(source, dest)) continue;
            await using var from = File.OpenRead(Path.Combine(backup, "files", rel));
            await using var to = File.Create(dest);
            await from.CopyToAsync(to, ct).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Combine(backup, "rolled-back.txt"), DateTimeOffset.UtcNow.ToString("O") + "\n", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an approved command repair: dotnet restore of the app project, or the workload install the project pins.
    /// The process gets the scrubbed build environment (no tokens or secrets). Output lines go to <paramref name="log"/>.
    /// </summary>
    public async Task<DoctorRepairRecord> RunCommandAsync(ProjectRecord project, DotNetIosProjectInfo app, DoctorRepair repair, string dotnet, Action<string> log, CancellationToken ct)
    {
        var source = Path.GetFullPath(project.SourceDirectory);
        var args = repair.Kind switch
        {
            RepairKind.RestorePackages => new List<string> { "restore", Path.Combine(source, app.ProjectPath) },
            RepairKind.InstallWorkload => WorkloadArguments(project, app),
            _ => throw new InvalidOperationException("This repair is not a command."),
        };
        var scrub = BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment();
        log($"$ dotnet {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var result = await runner.RunAsync(new ProcessSpec
        {
            Executable = dotnet, Arguments = args, WorkingDirectory = source, Environment = scrub.Environment, Timeout = TimeSpan.FromMinutes(30),
        }, (_, line) => log(line), ct).ConfigureAwait(false);
        var outcome = result.Succeeded ? "Completed" : result.TimedOut ? "Timed out" : result.Cancelled ? "Cancelled" : $"Failed (exit code {result.ExitCode})";
        log(outcome);
        return new DoctorRepairRecord(repair.Id, repair.Title, time.GetUtcNow(), null, [], result.Succeeded, outcome);
    }

    /// <summary>
    /// dotnet workload install ios, pinned to the project's version: its rollback file, or a rollback file HCG writes in
    /// the workspace from the manifest version the project declares (never the newest available by accident).
    /// </summary>
    public static List<string> WorkloadArguments(ProjectRecord project, DotNetIosProjectInfo app)
    {
        var args = new List<string> { "workload", "install", "ios" };
        if (app.WorkloadPin is not { } pin) return args;
        if (app.WorkloadPinIsRollbackFile && app.WorkloadPinFile is { } file)
            args.AddRange(["--from-rollback-file", Path.Combine(Path.GetFullPath(project.SourceDirectory), file)]);
        else if (pin.Contains('/'))
        {
            var dir = Path.Combine(project.WorkspacePath, "doctor");
            Directory.CreateDirectory(dir);
            var rollback = Path.Combine(dir, "ios-workload-rollback.json");
            File.WriteAllText(rollback, JsonSerializer.Serialize(new Dictionary<string, string> { ["microsoft.net.sdk.ios"] = pin }) + "\n");
            args.AddRange(["--from-rollback-file", rollback]);
        }
        return args;
    }

    /// <summary>
    /// Evaluates projects with MSBuild itself (dotnet msbuild -getProperty/-getItem): no targets run, so nothing is built
    /// or restored, but the project's evaluation logic (imports, conditions, property functions) runs exactly as in a
    /// build — which is why it needs the same trust confirmation as a build. Returns one line per evaluated fact.
    /// </summary>
    public async Task<IReadOnlyList<MsBuildEvaluation>> EvaluateWithMsBuildAsync(ProjectRecord project, IReadOnlyList<string> projectRels, string dotnet, CancellationToken ct)
    {
        var source = Path.GetFullPath(project.SourceDirectory);
        var scrub = BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment();
        var results = new List<MsBuildEvaluation>();
        foreach (var rel in projectRels.Distinct(StringComparer.Ordinal))
        {
            var output = new StringBuilder();
            var result = await runner.RunAsync(new ProcessSpec
            {
                Executable = dotnet, WorkingDirectory = source, Environment = scrub.Environment, Timeout = TimeSpan.FromMinutes(3),
                Arguments = ["msbuild", Path.Combine(source, rel), "-nologo", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks",
                    "-getProperty:ApplicationId", "-getProperty:ApplicationDisplayVersion", "-getProperty:RuntimeIdentifier", "-getItem:ProjectReference"],
            }, (_, line) => output.AppendLine(line), ct).ConfigureAwait(false);
            results.Add(MsBuildEvaluation.Parse(rel, source, result.Succeeded, output.ToString()));
        }
        return results;
    }

    private static string Slug(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').Take(48).ToArray());
}

/// <summary>What MSBuild itself evaluated for one project: properties and each project reference with whether it exists.</summary>
public sealed record MsBuildEvaluation(string Project, bool Succeeded, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<(string Path, bool Exists)> References, string? Error)
{
    public static MsBuildEvaluation Parse(string rel, string source, bool succeeded, string output)
    {
        var start = output.IndexOf('{');
        if (!succeeded || start < 0)
            return new(rel, false, new Dictionary<string, string>(), [], output.Trim().Split('\n').FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim() ?? output.Trim());
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("Properties", out var p))
                foreach (var kv in p.EnumerateObject()) if (kv.Value.GetString() is { Length: > 0 } v) props[kv.Name] = v;
            var refs = new List<(string, bool)>();
            if (doc.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("ProjectReference", out var pr))
                foreach (var item in pr.EnumerateArray())
                {
                    var full = item.TryGetProperty("FullPath", out var fp) ? fp.GetString() : item.GetProperty("Identity").GetString();
                    if (full is null) continue;
                    refs.Add((Path.GetRelativePath(source, full).Replace('\\', '/'), File.Exists(full)));
                }
            return new(rel, true, props, refs, null);
        }
        catch (JsonException ex) { return new(rel, false, new Dictionary<string, string>(), [], "MSBuild output was not JSON: " + ex.Message); }
    }
}
