using System.IO.Compression;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.Windows;

namespace HCG.ExecutableMaker.BuildEngine.Windows;

/// <summary>
/// Packages a project for Windows by following its own contract (<see cref="WindowsPackagingContract"/>), e.g. HCG
/// Server's portable ZIP and NSIS installer with SHA256SUMS and Release Passport build halves. Executable Maker:
/// <list type="number">
/// <item>refuses unless the person confirmed this exact contract (SHA-256);</item>
/// <item>checks every declared tool with Build Readiness's resolver and never installs one;</item>
/// <item>runs the declared steps in order, as argument lists (no shell), in the source copy, with <c>${STAGE}</c> set to a
/// fresh folder of its own;</item>
/// <item>collects only the declared outputs written by this run, and verifies them independently: SHA256SUMS against the
/// bytes, each Release Passport against the schema, the build-half rules and the file it describes, the installed
/// files inside the portable ZIP, and the PE machine of every executable against the declared architecture.</item>
/// </list>
/// Nothing is signed, uploaded or published. The passports are written by the project, not by Executable Maker.
/// </summary>
public sealed class WindowsContractPackager(IProcessRunner runner)
{
    public sealed record Request
    {
        public required string SourceRoot { get; init; }
        public required WindowsPackagingContract Contract { get; init; }
        /// <summary>The SHA-256 the person confirmed after reviewing the steps; it must equal the contract file's.</summary>
        public string? ConfirmedSha256 { get; init; }
        public required string OutputDirectory { get; init; }
        /// <summary>The steps' environment; null = Executable Maker's untrusted-build environment (secrets removed), as for every build.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; init; }
        public TimeSpan StepTimeout { get; init; } = TimeSpan.FromMinutes(45);
    }

    public sealed record Artifact(string Path, string Kind, string Sha256, long Size, IReadOnlyList<VerificationCheck> Checks)
    {
        public VerificationStatus Status => Packaging.Verification.Checks.Combine(Checks);
    }

    public sealed record Result(bool Succeeded, string Summary, IReadOnlyList<VerificationCheck> Readiness, IReadOnlyList<Artifact> Artifacts, IReadOnlyList<string> Log);

    /// <summary>What the person reviews before confirming: the file, its hash, the tools and every command exactly as it will run.</summary>
    public static IReadOnlyList<string> Review(WindowsPackagingContract c)
    {
        var lines = new List<string>
        {
            $"Windows packaging contract: {c.Path} ({WindowsPackagingContract.Schema})",
            $"SHA-256: {c.Sha256}",
            $"Product: {c.Product}{(c.Identifier is null ? "" : $" ({c.Identifier})")} · Windows {c.Architecture}{(c.RuntimeIdentifier is null ? "" : $" · {c.RuntimeIdentifier}")}",
            "Requires: " + (c.Requires.Count == 0 ? "(nothing declared)" : string.Join(", ", c.Requires.Select(r => r.MinimumVersion is null ? r.Tool : $"{r.Tool} >= {r.MinimumVersion}"))),
            "Steps (run in this order, in a copy of the source, as argument lists — never through a shell):",
        };
        var n = 1;
        foreach (var s in c.Steps)
        {
            lines.Add($"  {n++}. {s.Description}");
            lines.Add("     " + string.Join(" ", s.Command.Select(Quote)));
            if (s.Environment.Count > 0) lines.Add("     environment: " + string.Join(" ", s.Environment.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        lines.Add("Outputs: " + string.Join(", ", c.Outputs.Select(o => $"{o.Name} ({o.Kind})")));
        if (c.InstalledFiles.Count > 0) lines.Add($"Installed: {string.Join(", ", c.InstalledFiles)}{(c.InstallLocation is null ? "" : $" in {c.InstallLocation}")}");
        lines.Add("Code signing (as the contract states it): " + (c.CodeSigning ?? "not stated"));
        lines.Add("Executable Maker never signs, uploads or publishes these files.");
        return lines;
    }

    private static string Quote(string a) => a.Length > 0 && a.All(ch => char.IsLetterOrDigit(ch) || "-_./=${}:+,@%".Contains(ch)) ? a : "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public async Task<Result> RunAsync(Request request, Action<string>? progress, CancellationToken ct)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(line); progress?.Invoke(line); }
        var c = request.Contract;
        var readiness = new List<VerificationCheck>();

        // 1. The exact contract the person confirmed (it may have changed since the review).
        var file = Path.Combine(request.SourceRoot, c.Path.Replace('/', Path.DirectorySeparatorChar));
        var current = File.Exists(file) ? await Checksums.Sha256FileAsync(file, ct).ConfigureAwait(false) : null;
        if (request.ConfirmedSha256 is null || !string.Equals(request.ConfirmedSha256, c.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(false, $"Not run: the steps of {c.Path} have not been confirmed. Review them, then confirm SHA-256 {c.Sha256}.", readiness, [], log);
        if (!string.Equals(current, c.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(false, $"Not run: {c.Path} changed after it was confirmed (now {current ?? "missing"}). Review it again.", readiness, [], log);

        // 2. Every declared tool, through Build Readiness's resolver (found and probed; never installed).
        var resolver = new Readiness.ToolResolver(runner);
        var tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in c.ToolRequirements())
        {
            var def = Readiness.ToolCatalog.For(req.ToolId);
            var r = await resolver.ResolveAsync(def, req.MinimumVersion, [], ct).ConfigureAwait(false);
            var ok = r.State is ReadinessState.Ready or ReadinessState.NotVerifiable && r.Selected is not null;
            readiness.Add(new VerificationCheck($"{def.DisplayName}{(req.MinimumVersion is null ? "" : $" >= {req.MinimumVersion}")}",
                ok ? VerificationStatus.Passed : VerificationStatus.Failed, ok ? $"{r.Selected!.Version} at {r.Selected.Path} ({req.DeclaredBy})" : $"{r.Detail} ({req.DeclaredBy})"));
            if (ok) tools[def.Id] = r.Selected!.Path;
        }
        foreach (var x in readiness) Log($"Requirement {x.Name}: {x.Status} — {x.Detail}");
        if (readiness.Any(x => x.Status == VerificationStatus.Failed))
            return new(false, "Not run: " + string.Join("; ", readiness.Where(x => x.Status == VerificationStatus.Failed).Select(x => $"{x.Name}: {x.Detail}")) +
                ". Toolchains › Build Readiness shows how to get them; Executable Maker installs nothing by itself.", readiness, [], log);

        // 3. The steps, in order, as argument lists in the source copy.
        var stage = Path.Combine(request.OutputDirectory, ".stage");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);
        var started = DateTime.UtcNow.AddSeconds(-2);
        var pathDirs = tools.Values.Select(p => Path.GetDirectoryName(p)!).Distinct().ToList();
        var baseEnv = request.Environment ?? Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment;
        var basePath = baseEnv.GetValueOrDefault("PATH") ?? "";
        var n = 0;
        foreach (var step in c.Steps)
        {
            n++;
            var args = step.Command.Select(a => a.Replace("${STAGE}", stage, StringComparison.Ordinal)).ToList();
            var program = args[0].Contains('/')
                ? Path.GetFullPath(Path.Combine(request.SourceRoot, args[0]))
                : tools.GetValueOrDefault(Readiness.ToolCatalog.For(args[0]).Id) ?? args[0];
            if (args[0].Contains('/') && (!program.StartsWith(Path.GetFullPath(request.SourceRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(program)))
                return new(false, $"Step {n} ({step.Description}): {args[0]} is not a file in the source.", readiness, [], log);
            var env = new Dictionary<string, string>(baseEnv, StringComparer.Ordinal) { ["PATH"] = string.Join(Path.PathSeparator, pathDirs.Append(basePath)) };
            foreach (var kv in step.Environment) env[kv.Key] = kv.Value;
            Log($"Step {n}/{c.Steps.Count}: {step.Description}");
            Log("  $ " + string.Join(" ", args.Select(Quote)));
            var res = await runner.RunAsync(new ProcessSpec
            {
                Executable = program, Arguments = args.Skip(1).ToList(), WorkingDirectory = request.SourceRoot, Environment = env, Timeout = request.StepTimeout,
            }, (_, line) => Log("  " + line), ct).ConfigureAwait(false);
            if (!res.Succeeded)
                return new(false, $"Step {n} ({step.Description}) failed: {(res.TimedOut ? "timed out" : $"exit code {res.ExitCode}")}. See the log above.", readiness, [], log);
        }

        // 4. The declared outputs written by this run, copied out of the source copy and verified.
        var found = new List<(WindowsPackagingContract.Output Output, string Path)>();
        foreach (var f in Directory.EnumerateFiles(request.SourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true }))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || f.StartsWith(stage, StringComparison.Ordinal)) continue;
            if (File.GetLastWriteTimeUtc(f) < started) continue;
            if (c.Outputs.FirstOrDefault(o => WindowsPackagingContract.Matches(o.Name, Path.GetFileName(f))) is { } o) found.Add((o, f));
        }
        foreach (var o in c.Outputs.Where(o => o.Kind is "portable-zip" or "installer" or "checksums"))
            if (!found.Any(x => x.Output == o))
                return new(false, $"The steps finished, but no {o.Kind} named {o.Name} was written by this run.", readiness, [], log);
        Directory.CreateDirectory(request.OutputDirectory);
        foreach (var (_, src) in found) File.Copy(src, Path.Combine(request.OutputDirectory, Path.GetFileName(src)), overwrite: true);
        try { Directory.Delete(stage, recursive: true); } catch (IOException) { }

        var artifacts = await VerifyAsync(c, request.OutputDirectory, found.Select(x => (x.Output, Path.Combine(request.OutputDirectory, Path.GetFileName(x.Path)))).ToList(), ct).ConfigureAwait(false);
        foreach (var a in artifacts)
        {
            Log($"{Path.GetFileName(a.Path)} — {a.Kind} — {a.Size:N0} bytes — SHA-256 {a.Sha256} — {a.Status}");
            foreach (var x in a.Checks) Log($"  {x.Name}: {x.Status} — {x.Detail}");
        }
        var failed = artifacts.Where(a => a.Status == VerificationStatus.Failed).ToList();
        return new(failed.Count == 0,
            failed.Count == 0
                ? $"{artifacts.Count(a => a.Kind is "portable-zip" or "installer")} package(s) built by {c.Path} and verified. {c.CodeSigning ?? "Code signing not stated"}. Nothing was uploaded or published."
                : "Verification failed: " + string.Join("; ", failed.SelectMany(a => a.Checks.Where(x => x.Status == VerificationStatus.Failed).Select(x => $"{Path.GetFileName(a.Path)}: {x.Name} — {x.Detail}"))),
            readiness, artifacts, log);
    }

    /// <summary>Independent checks of what the contract produced (no trust in the project's own inspection).</summary>
    public static async Task<IReadOnlyList<Artifact>> VerifyAsync(WindowsPackagingContract c, string dir, IReadOnlyList<(WindowsPackagingContract.Output Output, string Path)> files, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, path) in files) hashes[path] = await Checksums.Sha256FileAsync(path, ct).ConfigureAwait(false);
        var sumsFile = files.FirstOrDefault(f => f.Output.Kind == "checksums").Path;
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sumsFile is not null)
            foreach (var line in File.ReadAllLines(sumsFile))
                if (line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries) is [var h, var name])
                    sums[name.TrimStart('*').Trim()] = h.ToLowerInvariant();

        var list = new List<Artifact>();
        foreach (var (output, path) in files)
        {
            var name = Path.GetFileName(path);
            var checks = new List<VerificationCheck>();
            if (output.Kind is "portable-zip" or "installer")
            {
                checks.Add(sums.TryGetValue(name, out var listed)
                    ? listed == hashes[path] ? new("SHA256SUMS", VerificationStatus.Passed, "listed with these bytes' SHA-256")
                                             : new("SHA256SUMS", VerificationStatus.Failed, $"lists {listed}, the file is {hashes[path]}")
                    : new("SHA256SUMS", VerificationStatus.Failed, sumsFile is null ? "no SHA256SUMS was produced" : $"{name} is not listed"));
                checks.Add(PassportCheck(path + ".release-passport.json", name, hashes[path], new FileInfo(path).Length, c));
                if (output.Kind == "portable-zip") checks.AddRange(ZipChecks(path, c));
                else checks.Add(PeCheck(path, "installer", expectBootstrapperArchitecture: null));
            }
            else if (output.Kind == "checksums")
                checks.Add(new("Format", sums.Count > 0 ? VerificationStatus.Passed : VerificationStatus.Failed, $"{sums.Count} entries"));
            else if (output.Kind == "release-passport-build-half")
                checks.Add(new("Schema", ReleasePassport.ValidateBuildHalf(JsonNode.Parse(File.ReadAllText(path))) is { Count: 0 } ? VerificationStatus.Passed : VerificationStatus.Failed,
                    "hcg.release-passport v1 build half"));
            list.Add(new Artifact(path, output.Kind, hashes[path], new FileInfo(path).Length, checks));
        }
        return list;
    }

    private static VerificationCheck PassportCheck(string passportPath, string name, string sha, long size, WindowsPackagingContract c)
    {
        if (!File.Exists(passportPath)) return new("Release Passport", VerificationStatus.Failed, $"{Path.GetFileName(passportPath)} was not produced");
        JsonNode? p;
        try { p = JsonNode.Parse(File.ReadAllText(passportPath)); }
        catch (System.Text.Json.JsonException ex) { return new("Release Passport", VerificationStatus.Failed, "not JSON: " + ex.Message); }
        var problems = ReleasePassport.ValidateBuildHalf(p).ToList();
        if (p?["artifact"]?["sha256"]?.GetValue<string>() != sha) problems.Add($"artifact.sha256 is not {sha}");
        if (p?["artifact"]?["filename"]?.GetValue<string>() != name) problems.Add($"artifact.filename is not {name}");
        if (p?["artifact"]?["size"]?.GetValue<long>() != size) problems.Add($"artifact.size is not {size}");
        if (p?["architecture"]?.GetValue<string>() is { } arch && arch != c.Architecture) problems.Add($"architecture {arch} is not the contract's {c.Architecture}");
        return problems.Count == 0
            ? new("Release Passport", VerificationStatus.Passed, $"valid build half for these bytes (source {p?["source"]?["commit"]?.GetValue<string>()?[..Math.Min(12, p["source"]!["commit"]!.GetValue<string>().Length)] ?? "no commit"}, provider {p?["build"]?["provider"]})")
            : new("Release Passport", VerificationStatus.Failed, string.Join("; ", problems.Take(4)));
    }

    private static IEnumerable<VerificationCheck> ZipChecks(string zipPath, WindowsPackagingContract c)
    {
        var checks = new List<VerificationCheck>();
        var scratch = Path.Combine(Path.GetTempPath(), "hcg-wincontract-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var unsafeEntry = entries.FirstOrDefault(e => e.FullName.StartsWith('/') || e.FullName.Split('/').Contains("..") || e.FullName.Contains('\\'));
            checks.Add(unsafeEntry is null ? new("ZIP paths", VerificationStatus.Passed, $"{entries.Count} files, none outside the archive")
                                           : new("ZIP paths", VerificationStatus.Failed, $"{unsafeEntry.FullName} would land outside the folder"));
            foreach (var want in c.InstalledFiles)
            {
                var e = entries.FirstOrDefault(x => Path.GetFileName(x.FullName) == want);
                checks.Add(e is null ? new($"Contains {want}", VerificationStatus.Failed, "missing from the portable ZIP")
                                     : new($"Contains {want}", VerificationStatus.Passed, e.FullName));
            }
            Directory.CreateDirectory(scratch);
            foreach (var e in entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var target = Path.Combine(scratch, Guid.NewGuid().ToString("N") + Path.GetExtension(e.FullName));
                e.ExtractToFile(target);
                var pe = PeCheck(target, e.FullName, c.Architecture);
                checks.Add(pe with { Name = $"PE {e.FullName}" });
            }
        }
        catch (InvalidDataException ex) { checks.Add(new("ZIP", VerificationStatus.Failed, "not a readable ZIP: " + ex.Message)); }
        finally { try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
        return checks;
    }

    /// <summary>A Windows executable's machine type; an installer's bootstrapper is commonly x86 and is reported, not required to match.</summary>
    private static VerificationCheck PeCheck(string path, string label, string? expectBootstrapperArchitecture)
    {
        var pe = PeFile.Read(path);
        if (pe is null) return new("PE", VerificationStatus.Failed, $"{label} is not a Windows executable (no PE header)");
        var signed = pe.HasCertificateTable ? "Authenticode signature present" : "unsigned";
        if (expectBootstrapperArchitecture is null)
            return new("PE", VerificationStatus.Passed, $"{label}: PE {pe.MachineName}{(pe.IsGui ? " GUI" : "")}, {signed} (an installer's bootstrapper may be x86; the programs it installs are checked in the ZIP)");
        return pe.MachineName == expectBootstrapperArchitecture
            ? new("PE", VerificationStatus.Passed, $"{label}: PE {pe.MachineName}{(pe.IsDotNet ? " .NET" : "")}{(pe.IsGui ? " GUI" : "")}, {signed}")
            : new("PE", VerificationStatus.Failed, $"{label}: PE {pe.MachineName}, not {expectBootstrapperArchitecture} as the contract declares");
    }
}
