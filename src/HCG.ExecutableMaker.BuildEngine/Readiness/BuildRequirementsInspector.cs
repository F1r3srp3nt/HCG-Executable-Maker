using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.BuildEngine.Readiness;

/// <summary>
/// Works out which tools a build needs, from the project's own files only: the packaging contract (AppPackaging.json)
/// and the scripts its prebuild steps run, go.mod, Package.swift, package.json, Cargo.toml, pyproject.toml,
/// CMakeLists.txt and global.json, plus the build kind and target chosen in HCG. Nothing in the project is executed.
/// </summary>
public static partial class BuildRequirementsInspector
{
    private const int MaxScriptBytes = 256 * 1024;

    /// <summary>Requirements of one configured build.</summary>
    public static IReadOnlyList<ToolRequirement> ForBuild(string sourceRoot, BuildSettings settings, ProjectAnalysis? analysis)
    {
        var list = new List<ToolRequirement>();
        if (settings.Swift is { } swift)
        {
            var pkgDir = Combine(sourceRoot, swift.PackageDirectory);
            var tools = analysis?.Swift?.Packages.FirstOrDefault(p => p.Directory == swift.PackageDirectory)?.ToolsVersion ?? SwiftToolsVersion(pkgDir);
            list.Add(new ToolRequirement
            {
                ToolId = "swift", DisplayName = "Swift", MinimumVersion = tools,
                DeclaredBy = Rel(swift.PackageDirectory, "Package.swift") + (tools is null ? "" : $" (swift-tools-version {tools})"),
                Purpose = $"swift build of {swift.Product}",
            });
            if (swift.PackagingContract is { } contract) list.AddRange(FromContract(sourceRoot, contract));
        }
        else if (settings.MacXcode is { } xcode)
        {
            list.Add(new ToolRequirement { ToolId = "xcode", DisplayName = "Xcode", DeclaredBy = xcode.ProjectPath, Purpose = "xcodebuild" });
        }
        else if (settings.Electron is { } electron)
        {
            var (node, where) = NodeEngine(Combine(sourceRoot, electron.ProjectDirectory));
            var pj = Rel(electron.ProjectDirectory, "package.json");
            list.Add(new ToolRequirement { ToolId = "node", DisplayName = "Node.js", MinimumVersion = node, DeclaredBy = pj + where, Purpose = "electron-builder and the project's scripts" });
            list.Add(new ToolRequirement { ToolId = "npm", DisplayName = "npm", DeclaredBy = pj, Purpose = "installing the project's dependencies" });
        }
        else if (settings.Native is { } native)
        {
            var dir = Combine(sourceRoot, native.ProjectDirectory);
            if (native.Kind == NativeProjectKind.Make)
                list.Add(new ToolRequirement { ToolId = "make", DisplayName = "make", DeclaredBy = Rel(native.ProjectDirectory, "Makefile"), Purpose = $"make {native.Target}" });
            else
                list.Add(new ToolRequirement
                {
                    ToolId = "cmake", DisplayName = "CMake", MinimumVersion = native.Kind == NativeProjectKind.CMake ? CMakeMinimum(dir) : null,
                    DeclaredBy = native.Kind == NativeProjectKind.CMake ? Rel(native.ProjectDirectory, "CMakeLists.txt") : "HCG's generated CMake configuration",
                    Purpose = "configure and build",
                });
        }
        else if (settings.Mobile is null)
        {
            var sdk = analysis?.GlobalJsonSdkVersion;
            list.Add(new ToolRequirement
            {
                ToolId = "dotnet", DisplayName = ".NET SDK", MinimumVersion = sdk,
                DeclaredBy = sdk is null ? settings.ProjectRelativePath : "global.json (sdk.version)", Purpose = "dotnet publish",
            });
        }
        return Merge(list);
    }

    /// <summary>
    /// Requirements of a project as a whole (no build configured yet): every packaging contract and manifest found near
    /// the root. Used by Toolchains › Build Readiness before the first build.
    /// </summary>
    public static IReadOnlyList<ToolRequirement> ForProject(string sourceRoot, ProjectAnalysis? analysis)
    {
        var list = new List<ToolRequirement>();
        foreach (var pkg in analysis?.Swift?.Packages ?? [])
        {
            list.Add(new ToolRequirement { ToolId = "swift", DisplayName = "Swift", MinimumVersion = pkg.ToolsVersion, DeclaredBy = pkg.ManifestPath, Purpose = "swift build" });
            var (contract, _) = SwiftPackagingContractReader.Read(sourceRoot, pkg.Directory);
            if (contract is not null) list.AddRange(FromContract(sourceRoot, contract));
        }
        if (analysis?.Swift is null)
            foreach (var manifest in Find(sourceRoot, "Package.swift", 3))
            {
                var dir = RelDir(sourceRoot, manifest);
                list.Add(new ToolRequirement { ToolId = "swift", DisplayName = "Swift", MinimumVersion = SwiftToolsVersion(Path.GetDirectoryName(manifest)!), DeclaredBy = Rel(dir, "Package.swift"), Purpose = "swift build" });
                var (contract, _) = SwiftPackagingContractReader.Read(sourceRoot, dir);
                if (contract is not null) list.AddRange(FromContract(sourceRoot, contract));
            }
        foreach (var gomod in Find(sourceRoot, "go.mod", 2))
            list.Add(GoModule(sourceRoot, gomod, "go build", null, "go.mod"));
        if (File.Exists(Path.Combine(sourceRoot, "Cargo.toml")))
        {
            var rust = TomlValue(Path.Combine(sourceRoot, "Cargo.toml"), "rust-version");
            list.Add(new ToolRequirement { ToolId = "cargo", DisplayName = "Cargo", MinimumVersion = rust, DeclaredBy = "Cargo.toml" + (rust is null ? "" : " (rust-version)"), Purpose = "cargo build" });
            list.Add(new ToolRequirement { ToolId = "rustc", DisplayName = "Rust (rustc)", MinimumVersion = rust, DeclaredBy = "Cargo.toml", Purpose = "compiling the crate" });
        }
        if (File.Exists(Path.Combine(sourceRoot, "pyproject.toml")))
        {
            var py = MinimumFromSpec(TomlValue(Path.Combine(sourceRoot, "pyproject.toml"), "requires-python"));
            list.Add(new ToolRequirement { ToolId = "python3", DisplayName = "Python", MinimumVersion = py, DeclaredBy = "pyproject.toml" + (py is null ? "" : " (requires-python)"), Purpose = "the Python project" });
        }
        if (File.Exists(Path.Combine(sourceRoot, "package.json")))
        {
            var (node, where) = NodeEngine(sourceRoot);
            list.Add(new ToolRequirement { ToolId = "node", DisplayName = "Node.js", MinimumVersion = node, DeclaredBy = "package.json" + where, Purpose = "the project's npm scripts" });
            list.Add(new ToolRequirement { ToolId = "npm", DisplayName = "npm", DeclaredBy = "package.json", Purpose = "installing dependencies" });
        }
        if (File.Exists(Path.Combine(sourceRoot, "CMakeLists.txt")))
            list.Add(new ToolRequirement { ToolId = "cmake", DisplayName = "CMake", MinimumVersion = CMakeMinimum(sourceRoot), DeclaredBy = "CMakeLists.txt", Purpose = "configure and build" });
        if (analysis?.Projects.Count > 0 || File.Exists(Path.Combine(sourceRoot, "global.json")))
            list.Add(new ToolRequirement { ToolId = "dotnet", DisplayName = ".NET SDK", MinimumVersion = analysis?.GlobalJsonSdkVersion, DeclaredBy = analysis?.GlobalJsonSdkVersion is null ? "the .NET project files" : "global.json (sdk.version)", Purpose = "dotnet publish" });
        if (analysis?.MacXcode?.Projects.Count > 0)
            list.Add(new ToolRequirement { ToolId = "xcode", DisplayName = "Xcode", DeclaredBy = analysis.MacXcode.Projects[0].ProjectPath, Purpose = "xcodebuild" });
        if (File.Exists(Path.Combine(sourceRoot, "Dockerfile")))
            list.Add(new ToolRequirement { ToolId = "docker", DisplayName = "Docker", DeclaredBy = "Dockerfile", Purpose = "container images (HCG's app builds do not use it)", Required = false });
        return Merge(list);
    }

    /// <summary>
    /// The tools a packaging contract's prebuild steps declare (exactly as declared), the Go module those steps build
    /// (go.mod's go/toolchain lines), tools their scripts check for (command -v …), and lipo when a helper must be
    /// universal.
    /// </summary>
    public static IEnumerable<ToolRequirement> FromContract(string sourceRoot, SwiftPackagingContract contract)
    {
        var contractDir = SwiftPackagingContractReader.ContractDirectory(contract.Path);
        foreach (var step in contract.Prebuild)
        {
            var where = $"{contract.Path} › prebuild “{step.Description}”";
            string? wdAbs = null;
            try { wdAbs = SwiftPackagingContractReader.Resolve(sourceRoot, contractDir, step.WorkingDirectory, "workingDirectory"); }
            catch (SwiftPackagingContractReader.ContractException) { }
            foreach (var r in step.Requires)
            {
                var def = ToolCatalog.For(r.Tool);
                if (def.Id == "go" && wdAbs is not null && FindUp(wdAbs, sourceRoot, "go.mod") is { } gomod)
                {
                    var req = GoModule(sourceRoot, gomod, step.Description, r.MinimumVersion, where + $" requires “{r}”");
                    yield return req;
                    continue;
                }
                yield return new ToolRequirement
                {
                    ToolId = def.Id, DisplayName = def.DisplayName, MinimumVersion = r.MinimumVersion,
                    DeclaredBy = where + $" requires “{r}”", Purpose = step.Description,
                };
            }
            if (wdAbs is not null && step.Command.Count > 0 && step.Command[0].Contains('/'))
            {
                string? script = null;
                try { script = SwiftPackagingContractReader.Resolve(sourceRoot, RelDir(sourceRoot, Path.Combine(wdAbs, "x")), step.Command[0], "command"); }
                catch (SwiftPackagingContractReader.ContractException) { }
                if (script is not null)
                    foreach (var tool in ScriptChecks(script).Where(t => !step.Requires.Any(r => ToolCatalog.For(r.Tool).Id == ToolCatalog.For(t).Id)))
                    {
                        var def = ToolCatalog.For(tool);
                        yield return new ToolRequirement
                        {
                            ToolId = def.Id, DisplayName = def.DisplayName,
                            DeclaredBy = $"{RelDir(sourceRoot, Path.Combine(script, "x"))} (the script checks `command -v {tool}`)",
                            Purpose = $"used by “{step.Description}” when the script needs it",
                            Required = false,
                        };
                    }
            }
        }
        foreach (var h in contract.Helpers.Where(h => h.Architectures.Count > 1))
            yield return new ToolRequirement
            {
                ToolId = "lipo", DisplayName = "lipo",
                DeclaredBy = $"{contract.Path} › helper {h.Name} ({string.Join(" + ", h.Architectures)})",
                Purpose = $"combine the {string.Join(" and ", h.Architectures)} slices of {h.Name} into one universal executable",
            };
    }

    /// <summary>Tools a shell script checks for before using them (command -v X, which X, type -P X, hash X).</summary>
    public static IReadOnlyList<string> ScriptChecks(string scriptPath)
    {
        try
        {
            var info = new FileInfo(scriptPath);
            if (!info.Exists || info.Length > MaxScriptBytes) return [];
            var text = File.ReadAllText(scriptPath);
            return ScriptCheckRegex().Matches(text).Select(m => m.Groups[1].Value)
                .Where(t => t is not ("sudo" or "bash" or "sh" or "env")).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>go.mod: "go 1.25.0" (the minimum, raised to the declared one if higher) and "toolchain go1.25.4" (a note).</summary>
    public static ToolRequirement GoModule(string sourceRoot, string goModPath, string purpose, string? declaredMinimum, string declaredBy)
    {
        string? goLine = null, toolchain = null;
        try
        {
            foreach (var line in File.ReadLines(goModPath).Take(400))
            {
                if (GoDirectiveRegex().Match(line) is { Success: true } g) goLine ??= g.Groups[1].Value;
                if (GoToolchainRegex().Match(line) is { Success: true } t) toolchain ??= t.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var min = declaredMinimum;
        if (goLine is not null && (min is null || SwiftCapabilities.Compare(goLine, min) > 0)) min = goLine;
        var mod = RelDir(sourceRoot, Path.Combine(goModPath, "x"));
        var notes = new List<string>();
        if (goLine is not null) notes.Add($"{mod} declares go {goLine}.");
        if (toolchain is not null)
            notes.Add($"{mod} names toolchain go{toolchain}: with Go's default GOTOOLCHAIN=auto an older local Go downloads it from proxy.golang.org during the build.");
        return new ToolRequirement
        {
            ToolId = "go", DisplayName = "Go", MinimumVersion = min,
            DeclaredBy = declaredBy == "go.mod" ? mod : declaredBy + $"; {mod}" + (goLine is null ? "" : $" (go {goLine})"),
            Purpose = purpose, Notes = notes,
        };
    }

    /// <summary>One row per tool: the highest minimum wins, sources are listed together, required if any source requires it.</summary>
    public static IReadOnlyList<ToolRequirement> Merge(IEnumerable<ToolRequirement> items) =>
        items.GroupBy(r => r.ToolId, StringComparer.Ordinal).Select(g =>
        {
            var first = g.First();
            var min = g.Select(r => r.MinimumVersion).OfType<string>().OrderByDescending(v => v, Comparer<string>.Create(SwiftCapabilities.Compare)).FirstOrDefault();
            return first with
            {
                MinimumVersion = min,
                Required = g.Any(r => r.Required),
                DeclaredBy = string.Join("; ", g.Select(r => r.DeclaredBy).Distinct()),
                Purpose = string.Join("; ", g.Select(r => r.Purpose).Distinct()),
                Notes = g.SelectMany(r => r.Notes).Distinct().ToList(),
            };
        }).ToList();

    public static string? SwiftToolsVersion(string packageDir)
    {
        try
        {
            var manifest = Path.Combine(packageDir, "Package.swift");
            if (!File.Exists(manifest)) return null;
            var first = File.ReadLines(manifest).FirstOrDefault() ?? "";
            return SwiftToolsRegex().Match(first) is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>package.json engines.node → its lowest version (">=18.17 &lt;23" → 18.17; "^20.11" → 20.11).</summary>
    public static (string? Minimum, string Where) NodeEngine(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "package.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return (null, "");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("engines", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String)
                return (MinimumFromSpec(n.GetString()), $" (engines.node “{n.GetString()}”)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return (null, "");
    }

    /// <summary>The lowest version a requirement spec allows: "&gt;=3.11" → 3.11, "^20.11" → 20.11, "~=3.10" → 3.10.</summary>
    public static string? MinimumFromSpec(string? spec) =>
        spec is null ? null : SpecRegex().Match(spec) is { Success: true } m ? m.Groups[1].Value : null;

    public static string? CMakeMinimum(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "CMakeLists.txt");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path).Take(200))
                if (CMakeMinRegex().Match(line) is { Success: true } m) return m.Groups[1].Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? TomlValue(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(500))
            {
                var m = Regex.Match(line, "^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"([^\"]+)\"");
                if (m.Success) return MinimumFromSpec(m.Groups[1].Value) ?? m.Groups[1].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static IEnumerable<string> Find(string root, string name, int depth)
    {
        var results = new List<string>();
        void Walk(string dir, int d)
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) results.Add(f);
            if (d == 0) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var s in subs)
            {
                var n = Path.GetFileName(s);
                if (n.StartsWith('.') || n is "node_modules" or "build" or "dist" or "vendor" or "Pods" or "DerivedData") continue;
                Walk(s, d - 1);
            }
        }
        if (Directory.Exists(root)) Walk(root, depth);
        return results;
    }

    private static string? FindUp(string dir, string root, string name)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var d = Path.GetFullPath(dir); d.StartsWith(rootFull, StringComparison.Ordinal); d = Path.GetDirectoryName(d) ?? "")
        {
            var f = Path.Combine(d, name);
            if (File.Exists(f)) return f;
            if (d.Length <= rootFull.Length) break;
        }
        return null;
    }

    private static string Combine(string root, string rel) => rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string Rel(string dir, string file) => dir.Length == 0 ? file : $"{dir.TrimEnd('/')}/{file}";

    /// <summary>The '/'-separated folder of <paramref name="fileUnderDir"/> relative to <paramref name="root"/>.</summary>
    private static string RelDir(string root, string fileUnderDir)
    {
        var rel = Path.GetRelativePath(root, Path.GetDirectoryName(fileUnderDir)!).Replace(Path.DirectorySeparatorChar, '/');
        return rel == "." ? "" : rel;
    }

    [GeneratedRegex(@"(?:command\s+-v|which|type\s+-[pP]|hash)\s+([A-Za-z][A-Za-z0-9_.+-]*)")]
    private static partial Regex ScriptCheckRegex();
    [GeneratedRegex(@"^\s*go\s+(\d+\.\d+(?:\.\d+)?)\s*$")]
    private static partial Regex GoDirectiveRegex();
    [GeneratedRegex(@"^\s*toolchain\s+go(\d+\.\d+(?:\.\d+)?)\s*$")]
    private static partial Regex GoToolchainRegex();
    [GeneratedRegex(@"swift-tools-version\s*:\s*(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex SwiftToolsRegex();
    [GeneratedRegex(@"(\d+(?:\.\d+){0,2})")]
    private static partial Regex SpecRegex();
    [GeneratedRegex(@"cmake_minimum_required\s*\(\s*VERSION\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CMakeMinRegex();
}
