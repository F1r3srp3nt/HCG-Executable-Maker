using System.Buffers.Binary;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.BuildEngine.Native;

public enum NativeImageKind { Executable, Library, Unknown }

/// <summary>
/// Checks the produced file before anything is packaged: that it is an executable image (not a library, archive or
/// script) for this host's format and the chosen architecture, and which shared libraries it needs at run time.
/// Read only: the binary is never run (library needs are read with readelf / otool, which only parse the file).
/// </summary>
public static partial class NativeBinaryVerifier
{
    /// <summary>ELF EXEC, or DYN with an interpreter (PIE); Mach-O MH_EXECUTE; PE without the DLL flag.</summary>
    public static (NativeImageKind Kind, string Detail) ImageKind(string path)
    {
        byte[] data;
        try
        {
            using var f = File.OpenRead(path);
            data = new byte[Math.Min(f.Length, 1 << 20)];
            f.ReadExactly(data);
        }
        catch (IOException ex) { return (NativeImageKind.Unknown, ex.Message); }
        var span = data.AsSpan();
        if (span.Length >= 64 && span[0] == 0x7F && span[1] == (byte)'E' && span[2] == (byte)'L' && span[3] == (byte)'F')
        {
            var is64 = span[4] == 2;
            var le = span[5] == 1;
            ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o));
            uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o));
            ulong U64(int o) => le ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(o)) : BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(o));
            var type = U16(16);
            if (type == 2) return (NativeImageKind.Executable, "ELF executable");
            if (type != 3) return (NativeImageKind.Unknown, type == 1 ? "ELF relocatable object (.o), not a linked program" : $"ELF type {type}");
            var phoff = is64 ? (long)U64(32) : U32(28);
            var phentsize = U16(is64 ? 54 : 42);
            var phnum = U16(is64 ? 56 : 44);
            for (var i = 0; i < phnum; i++)
            {
                var o = phoff + (long)i * phentsize;
                if (o + 4 > span.Length) break;
                if (U32((int)o) == 3) return (NativeImageKind.Executable, "ELF position-independent executable (PIE)");
            }
            return (NativeImageKind.Library, "ELF shared library (no program interpreter)");
        }
        if (span.Length >= 16)
        {
            var magicBe = BinaryPrimitives.ReadUInt32BigEndian(span);
            if (magicBe is 0xCAFEBABE or 0xCAFEBABF && span.Length >= 28)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                return offset + 16 <= span.Length ? MachOKind(span[offset..]) : (NativeImageKind.Unknown, "universal Mach-O (slice beyond the read range)");
            }
            var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (magicLe is 0xFEEDFACF or 0xFEEDFACE) return MachOKind(span);
        }
        if (span.Length >= 0x40 && span[0] == (byte)'M' && span[1] == (byte)'Z')
        {
            var pe = BinaryPrimitives.ReadInt32LittleEndian(span[0x3C..]);
            if (pe > 0 && pe + 24 <= span.Length && span[pe] == (byte)'P' && span[pe + 1] == (byte)'E')
            {
                var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 22)..]);
                return (characteristics & 0x2000) != 0 ? (NativeImageKind.Library, "Windows DLL") : (NativeImageKind.Executable, "Windows executable (PE)");
            }
        }
        if (span.Length >= 8 && span[..8].SequenceEqual("!<arch>\n"u8)) return (NativeImageKind.Library, "static library archive (.a)");
        if (span.Length >= 2 && span[0] == (byte)'#' && span[1] == (byte)'!') return (NativeImageKind.Unknown, "a script, not a compiled program");
        return (NativeImageKind.Unknown, "not a recognized executable format");
    }

    private static (NativeImageKind, string) MachOKind(ReadOnlySpan<byte> slice)
    {
        var fileType = BinaryPrimitives.ReadUInt32LittleEndian(slice[12..]);
        return fileType switch
        {
            2 => (NativeImageKind.Executable, "Mach-O executable"),
            6 => (NativeImageKind.Library, "Mach-O dynamic library (.dylib)"),
            8 => (NativeImageKind.Library, "Mach-O bundle (plug-in)"),
            _ => (NativeImageKind.Unknown, $"Mach-O file type {fileType}"),
        };
    }

    /// <summary>The checks that decide whether the build output may be packaged at all.</summary>
    public static IReadOnlyList<VerificationCheck> VerifyExecutable(string path, TargetPlatform platform, TargetArchitecture arch)
    {
        var checks = new List<VerificationCheck>();
        if (!File.Exists(path))
        {
            checks.Add(new VerificationCheck("Output present", VerificationStatus.Failed, $"{path} does not exist."));
            return checks;
        }
        checks.Add(new VerificationCheck("Output present", VerificationStatus.Passed, $"{Path.GetFileName(path)}, {new FileInfo(path).Length:N0} bytes"));
        var (kind, detail) = ImageKind(path);
        checks.Add(kind == NativeImageKind.Executable
            ? new VerificationCheck("Executable image", VerificationStatus.Passed, detail)
            : new VerificationCheck("Executable image", VerificationStatus.Failed, $"{Path.GetFileName(path)} is {detail}, not an executable program. Choose an executable target or output."));
        checks.Add(BinaryInspector.CheckMatchesTarget(path, platform, arch));
        return checks;
    }

    // ------------------------------------------------------------------ runtime libraries

    public sealed record RuntimeLibrary(string Name, string Classification);

    public sealed record RuntimeReport(IReadOnlyList<RuntimeLibrary> Libraries, IReadOnlyList<string> SearchPaths, VerificationCheck Check, string Text);

    private static readonly string[] LinuxSystemPrefixes =
    [
        "libc.so", "libm.so", "libpthread.so", "libdl.so", "librt.so", "libgcc_s.so", "libstdc++.so", "ld-linux", "libresolv.so", "libutil.so", "linux-vdso", "linux-gate",
    ];

    /// <summary>
    /// Lists the shared libraries the executable needs and how each is satisfied: the operating system's own libraries,
    /// libraries HCG copied next to it (the project's own shared libraries), or external libraries that must already be
    /// installed where the program runs (not bundled). A copied library is only found if the executable searches its
    /// own folder ($ORIGIN / @executable_path / Windows' default), which is checked too.
    /// </summary>
    public static async Task<RuntimeReport> InspectRuntimeAsync(IProcessRunner runner, string executable, TargetPlatform host, IReadOnlyCollection<string> bundled, CancellationToken ct)
    {
        var bundledNames = bundled.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (host == TargetPlatform.Windows)
            return new RuntimeReport([], [], new VerificationCheck("Runtime libraries", VerificationStatus.NotVerifiable,
                "DLL dependencies are not read on Windows in this version. Programs built with MSVC need the Microsoft Visual C++ Redistributable on the target PC unless they link the runtime statically (/MT)."),
                "Runtime libraries: not inspected on Windows.\n");
        var tool = host == TargetPlatform.MacOS ? "/usr/bin/otool" : new ExecutableLocator().Find("readelf");
        if (tool is null || !File.Exists(tool))
            return new RuntimeReport([], [], new VerificationCheck("Runtime libraries", VerificationStatus.NotVerifiable,
                host == TargetPlatform.MacOS ? "otool was not found (Command Line Tools)." : "readelf was not found (binutils): the shared libraries the program needs were not checked."), "Runtime libraries: not inspected.\n");

        var output = new List<string>();
        async Task Run(params string[] args) => await runner.RunAsync(new ProcessSpec { Executable = tool, Arguments = [.. args, executable], Timeout = TimeSpan.FromMinutes(1) },
            (_, l) => { lock (output) output.Add(l); }, ct).ConfigureAwait(false);
        var libraries = new List<RuntimeLibrary>();
        var searchPaths = new List<string>();
        if (host == TargetPlatform.MacOS)
        {
            await Run("-L").ConfigureAwait(false);
            foreach (var line in output.Skip(1))
                if (OtoolLibraryRegex().Match(line) is { Success: true } m) libraries.Add(Classify(m.Groups["p"].Value, host, bundledNames));
            output.Clear();
            await Run("-l").ConfigureAwait(false);
            for (var i = 0; i < output.Count; i++)
                if (output[i].Contains("LC_RPATH", StringComparison.Ordinal))
                    for (var j = i + 1; j < Math.Min(i + 4, output.Count); j++)
                        if (OtoolPathRegex().Match(output[j]) is { Success: true } p) { searchPaths.Add(p.Groups["p"].Value); break; }
        }
        else
        {
            await Run("-d", "-W").ConfigureAwait(false);
            foreach (var line in output)
            {
                if (ReadelfNeededRegex().Match(line) is { Success: true } n) libraries.Add(Classify(n.Groups["n"].Value, host, bundledNames));
                else if (ReadelfRunpathRegex().Match(line) is { Success: true } r) searchPaths.AddRange(r.Groups["p"].Value.Split(':', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        // The executable itself is not a dependency (otool -L lists a dylib's own install name first).
        libraries = libraries.Where(l => l.Name != Path.GetFileName(executable)).DistinctBy(l => l.Name).ToList();

        var external = libraries.Where(l => l.Classification.StartsWith("external", StringComparison.Ordinal)).ToList();
        var bundledLibs = libraries.Where(l => l.Classification.StartsWith("bundled", StringComparison.Ordinal)).ToList();
        var searchesOwnFolder = host == TargetPlatform.MacOS
            ? searchPaths.Any(p => p.StartsWith("@executable_path", StringComparison.Ordinal) || p.StartsWith("@loader_path", StringComparison.Ordinal))
              || libraries.Where(l => bundledNames.Contains(Path.GetFileName(l.Name))).All(l => l.Name.StartsWith("@executable_path", StringComparison.Ordinal) || l.Name.StartsWith("@loader_path", StringComparison.Ordinal))
            : searchPaths.Any(p => p.Contains("$ORIGIN", StringComparison.Ordinal));
        var problems = new List<string>();
        if (external.Count > 0)
            problems.Add($"needs {string.Join(", ", external.Select(e => e.Name))}, which {(external.Count == 1 ? "is" : "are")} not part of the operating system and not bundled: install {(external.Count == 1 ? "it" : "them")} where the program runs, or link statically");
        if (bundledLibs.Count > 0 && !searchesOwnFolder)
            problems.Add($"the project's own {string.Join(", ", bundledLibs.Select(b => Path.GetFileName(b.Name)))} {(bundledLibs.Count == 1 ? "is" : "are")} copied next to the executable, but the executable does not search its own folder " +
                         $"({(host == TargetPlatform.MacOS ? "no @executable_path/@loader_path rpath" : "no $ORIGIN in RUNPATH")}{(searchPaths.Count > 0 ? "; it searches " + string.Join(", ", searchPaths) : "")}). It finds them only with {(host == TargetPlatform.MacOS ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH")} or if the project sets an $ORIGIN-relative install RPATH");
        var systemNote = host == TargetPlatform.Linux ? " System libraries (glibc, libstdc++) must be at least the versions of the build machine." : "";
        var check = problems.Count == 0
            ? new VerificationCheck("Runtime libraries", VerificationStatus.Passed,
                $"{libraries.Count} shared librar{(libraries.Count == 1 ? "y" : "ies")} needed: {(libraries.Count == 0 ? "none" : string.Join(", ", libraries.Select(l => $"{Path.GetFileName(l.Name)} ({l.Classification})")))}.{systemNote}")
            : new VerificationCheck("Runtime libraries", VerificationStatus.PassedWithWarnings, "Not portable as it is: " + string.Join("; ", problems) + "." + systemNote);
        var text = new System.Text.StringBuilder("Runtime libraries of " + Path.GetFileName(executable) + " (read with " + Path.GetFileName(tool) + "; the program was not run)\n");
        foreach (var l in libraries) text.Append("  ").Append(l.Name).Append(" — ").Append(l.Classification).Append('\n');
        if (searchPaths.Count > 0) text.Append("Search paths (RUNPATH/RPATH): ").Append(string.Join(", ", searchPaths)).Append('\n');
        text.Append(check.Status == VerificationStatus.Passed ? "Result: " : "Warning: ").Append(check.Detail).Append('\n');
        return new RuntimeReport(libraries, searchPaths, check, text.ToString());
    }

    private static RuntimeLibrary Classify(string name, TargetPlatform host, IReadOnlySet<string> bundled)
    {
        var file = Path.GetFileName(name);
        if (bundled.Contains(file)) return new(name, "bundled: the project's own library, copied next to the executable");
        if (host == TargetPlatform.MacOS)
            return name.StartsWith("/usr/lib/", StringComparison.Ordinal) || name.StartsWith("/System/Library/", StringComparison.Ordinal)
                ? new(name, "macOS system library")
                : new(name, "external: not part of macOS");
        return LinuxSystemPrefixes.Any(p => file.StartsWith(p, StringComparison.Ordinal))
            ? new(name, "system library (C/C++ runtime)")
            : new(name, "external: must be installed where the program runs");
    }

    [GeneratedRegex(@"\(NEEDED\)\s+Shared library: \[(?<n>[^\]]+)\]")] private static partial Regex ReadelfNeededRegex();
    [GeneratedRegex(@"\((?:RUNPATH|RPATH)\)\s+Library r(?:un)?path: \[(?<p>[^\]]*)\]")] private static partial Regex ReadelfRunpathRegex();
    [GeneratedRegex(@"^\s+(?<p>\S.*?) \(compatibility version")] private static partial Regex OtoolLibraryRegex();
    [GeneratedRegex(@"^\s+path (?<p>\S+)")] private static partial Regex OtoolPathRegex();
}
