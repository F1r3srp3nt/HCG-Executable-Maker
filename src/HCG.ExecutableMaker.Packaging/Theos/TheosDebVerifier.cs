using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.Theos;

/// <summary>
/// Reads and checks a Debian package built by Theos, without installing it: the ar container, control metadata, the
/// payload's install prefix for the chosen scheme (rootless /var/jb, rootful /), the files each Theos instance must
/// produce, and every Mach-O binary's architectures, arm64e pointer-authentication ABI and minimum iOS version.
/// gzip members are read in-process; xz/lzma/zstd/bzip2 members (Theos' dm.pl defaults to lzma) are unpacked with the
/// system tar (bsdtar on macOS, GNU tar on Linux) into a private temporary folder, with an argument list and a size cap.
/// </summary>
public static partial class TheosDebVerifier
{
    public const long MaxMemberBytes = 1L << 30;
    private static readonly string[] MaintainerScripts = ["preinst", "postinst", "prerm", "postrm", "extrainst_"];

    public sealed record ArMember(string Name, long Offset, long Size);

    public sealed record Result(IReadOnlyDictionary<string, string> Control, IReadOnlyList<string> Payload, IReadOnlyList<VerificationCheck> Checks, string Report);

    /// <summary>Members of an ar archive (the .deb container). Throws InvalidDataException when it is not one.</summary>
    public static IReadOnlyList<ArMember> ReadAr(Stream s)
    {
        var magic = new byte[8];
        s.ReadExactly(magic);
        if (Encoding.ASCII.GetString(magic) != "!<arch>\n") throw new InvalidDataException("Not a Debian package: the file does not start with the ar signature (!<arch>).");
        var members = new List<ArMember>();
        var header = new byte[60];
        while (s.Position < s.Length && members.Count < 16)
        {
            if (s.Read(header, 0, 60) < 60) break;
            var name = Encoding.ASCII.GetString(header, 0, 16).Trim().TrimEnd('/');
            if (!long.TryParse(Encoding.ASCII.GetString(header, 48, 10).Trim(), out var size) || size < 0 || s.Position + size > s.Length)
                throw new InvalidDataException($"The ar member header for '{name}' is damaged.");
            members.Add(new ArMember(name, s.Position, size));
            s.Position += size + (size % 2);
        }
        return members;
    }

    public static async Task<Result> VerifyAsync(string debPath, TheosSchemeChoice expected, IProcessRunner runner, CancellationToken ct)
    {
        var checks = new List<VerificationCheck>();
        var report = new StringBuilder();
        var temp = Directory.CreateTempSubdirectory("hcg-deb-").FullName;
        try
        {
            IReadOnlyList<ArMember> members;
            await using (var fs = File.OpenRead(debPath)) members = ReadAr(fs);
            var debianBinary = members.FirstOrDefault(m => m.Name == "debian-binary");
            var controlMember = members.FirstOrDefault(m => m.Name.StartsWith("control.tar", StringComparison.Ordinal));
            var dataMember = members.FirstOrDefault(m => m.Name.StartsWith("data.tar", StringComparison.Ordinal));
            string? version = null;
            if (debianBinary is not null)
            {
                await using var fs = File.OpenRead(debPath);
                fs.Position = debianBinary.Offset;
                var buf = new byte[Math.Min(16, debianBinary.Size)];
                fs.ReadExactly(buf);
                version = Encoding.ASCII.GetString(buf).Trim();
            }
            if (version != "2.0" || controlMember is null || dataMember is null)
            {
                checks.Add(new("Debian archive", VerificationStatus.Failed,
                    $"Expected debian-binary 2.0, control.tar.* and data.tar.*; found {string.Join(", ", members.Select(m => m.Name))} (debian-binary {version ?? "missing"})."));
                return new(new Dictionary<string, string>(), [], checks, "");
            }
            checks.Add(new("Debian archive", VerificationStatus.Passed, $"Format 2.0 · {controlMember.Name} · {dataMember.Name} ({dataMember.Size:N0} bytes compressed)"));

            var controlDir = Path.Combine(temp, "control");
            var dataDir = Path.Combine(temp, "data");
            await ExtractAsync(debPath, controlMember, controlDir, runner, ct).ConfigureAwait(false);
            await ExtractAsync(debPath, dataMember, dataDir, runner, ct).ConfigureAwait(false);

            // Control metadata.
            var controlFile = Path.Combine(controlDir, "control");
            var fields = File.Exists(controlFile) ? ParseControl(await File.ReadAllTextAsync(controlFile, ct).ConfigureAwait(false)) : new Dictionary<string, string>();
            report.AppendLine("Control:");
            foreach (var (k, v) in fields) report.AppendLine($"  {k}: {v}");
            var package = fields.GetValueOrDefault("Package");
            var debVersion = fields.GetValueOrDefault("Version");
            var metadataProblems = new List<string>();
            if (package is null) metadataProblems.Add("no Package field");
            else if (expected.PackageId is { } id && package != id) metadataProblems.Add($"Package is {package}, the project's control file says {id}");
            if (debVersion is null) metadataProblems.Add("no Version field");
            else if (expected.PackageVersion is { } pv && debVersion != pv && !debVersion.StartsWith(pv + "-", StringComparison.Ordinal) && !debVersion.StartsWith(pv + "+", StringComparison.Ordinal))
                metadataProblems.Add($"Version is {debVersion}, the project's control file says {pv}");
            checks.Add(new("Package metadata", metadataProblems.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                metadataProblems.Count == 0
                    ? $"{package} {debVersion} · {fields.GetValueOrDefault("Name") ?? "(no Name)"} · Depends: {fields.GetValueOrDefault("Depends") ?? "none"}"
                    : string.Join("; ", metadataProblems)));
            var arch = fields.GetValueOrDefault("Architecture");
            var expectedArch = new TheosScheme { PackageScheme = expected.PackageScheme }.DebArchitecture;
            checks.Add(new("Package architecture", arch == expectedArch ? VerificationStatus.Passed : VerificationStatus.Failed,
                arch == expectedArch ? $"Architecture: {arch} ({expected.PackageScheme} package)" : $"Architecture is {arch ?? "missing"}; a {expected.PackageScheme} package must say {expectedArch}."));

            var scripts = MaintainerScripts.Where(n => File.Exists(Path.Combine(controlDir, n))).ToList();
            checks.Add(scripts.Count == 0
                ? new("Maintainer scripts", VerificationStatus.Passed, "None: installing the package runs no script (no respring, no killall).")
                : new("Maintainer scripts", VerificationStatus.PassedWithWarnings, $"{string.Join(", ", scripts)} run as root when the package is installed. Review them before installing."));

            // Payload and install prefix.
            var payload = Directory.EnumerateFileSystemEntries(dataDir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(dataDir, p).Replace('\\', '/')).Where(p => p != ".").Order(StringComparer.Ordinal).ToList();
            var files = payload.Where(p => File.Exists(Path.Combine(dataDir, p)) || new FileInfo(Path.Combine(dataDir, p)).LinkTarget is not null).ToList();
            report.AppendLine("Payload:");
            foreach (var p in payload.Take(400)) report.AppendLine("  /" + p);
            var rootless = expected.PackageScheme == "rootless";
            var outside = rootless ? files.Where(f => !f.StartsWith("var/jb/", StringComparison.Ordinal)).ToList() : files.Where(f => f.StartsWith("var/jb/", StringComparison.Ordinal)).ToList();
            checks.Add(new("Install location", outside.Count == 0 && files.Count > 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                files.Count == 0 ? "The package installs no files."
                : outside.Count == 0 ? (rootless ? $"All {files.Count} files install under /var/jb (rootless)." : $"All {files.Count} files install in the rootful layout (none under /var/jb).")
                : (rootless ? "Files outside /var/jb in a rootless package: " : "Files under /var/jb in a rootful package: ") + string.Join(", ", outside.Take(8).Select(f => "/" + f))));

            // Expected products and their binaries.
            var prefix = rootless ? "var/jb/" : "";
            foreach (var instance in expected.Instances)
            {
                var (binary, extra) = ExpectedPaths(instance, prefix);
                var missing = new[] { binary }.Concat(extra).Where(p => !files.Contains(p)).ToList();
                checks.Add(new($"{instance.Type} {instance.Name}", missing.Count == 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                    missing.Count == 0 ? $"/{binary}" + (extra.Count > 0 ? " + " + string.Join(", ", extra.Select(e => "/" + e)) : "")
                                       : "Missing from the payload: " + string.Join(", ", missing.Select(m => "/" + m))));
                if (missing.Contains(binary)) continue;
                checks.Add(BinaryCheck(instance.Name, Path.Combine(dataDir, binary), binary, expected, report));
                if (instance.Type == TheosInstanceType.Tweak && extra.FirstOrDefault(e => e.EndsWith(".plist", StringComparison.Ordinal)) is { } filter && files.Contains(filter))
                    checks.Add(FilterCheck(instance.Name, Path.Combine(dataDir, filter)));
            }
            checks.Add(new("Package kind", VerificationStatus.Passed,
                "Debian package for jailbroken devices (Sileo, Zebra, Filza or dpkg). It is not an IPA: it cannot be installed through the App Store, TestFlight or IPA sideloading."));
            checks.Add(new("Installation", VerificationStatus.NotVerifiable, "Not installed: HCG never installs packages, resprings or touches a device. Install it yourself (see INSTALL.txt next to the package)."));
            return new(fields, payload, checks, report.ToString());
        }
        catch (InvalidDataException ex)
        {
            checks.Add(new("Debian archive", VerificationStatus.Failed, ex.Message));
            return new(new Dictionary<string, string>(), [], checks, report.ToString());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Where Theos installs an instance's binary (and, for tweaks, its filter plist), under the package prefix.</summary>
    public static (string Binary, IReadOnlyList<string> Extra) ExpectedPaths(TheosInstance i, string prefix)
    {
        string Dir(string fallback) => prefix + (i.InstallPath?.Trim('/') is { Length: > 0 } p && !p.Contains("$(", StringComparison.Ordinal) ? p : fallback);
        return i.Type switch
        {
            TheosInstanceType.Tweak => ($"{prefix}Library/MobileSubstrate/DynamicLibraries/{i.Name}.dylib", [$"{prefix}Library/MobileSubstrate/DynamicLibraries/{i.Name}.plist"]),
            TheosInstanceType.PreferenceBundle => ($"{Dir("Library/PreferenceBundles")}/{i.Name}.bundle/{i.Name}", []),
            TheosInstanceType.Bundle => ($"{Dir("Library/Bundles")}/{i.Name}.bundle/{i.Name}", []),
            TheosInstanceType.Application => ($"{Dir("Applications")}/{i.Name}.app/{i.Name}", [$"{Dir("Applications")}/{i.Name}.app/Info.plist"]),
            TheosInstanceType.Tool => ($"{Dir("usr/bin")}/{i.Name}", []),
            TheosInstanceType.Library => ($"{Dir("usr/lib")}/{i.Name}.dylib", []),
            _ => ($"{Dir("Library/Frameworks")}/{i.Name}.framework/{i.Name}", []),
        };
    }

    /// <summary>Architectures the project asked for, arm64e ABI (iOS 14+ needs the versioned one) and the minimum iOS.</summary>
    internal static VerificationCheck BinaryCheck(string name, string path, string rel, TheosSchemeChoice expected, StringBuilder report)
    {
        IReadOnlyList<AppleMachO.Slice> slices;
        try { slices = AppleMachO.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new($"{name} binary", VerificationStatus.Failed, $"/{rel} could not be read: {ex.Message}"); }
        if (slices.Count == 0) return new($"{name} binary", VerificationStatus.Failed, $"/{rel} is not a Mach-O binary.");
        report.AppendLine($"/{rel}: " + string.Join("; ", slices.Select(s => $"{s.Architecture}{(s.IsArm64e ? (s.PtrAuthVersioned ? $" (ptrauth ABI v{s.PtrAuthAbiVersion})" : " (old arm64e ABI)") : "")} {s.Type} {s.Platform} {s.MinimumOS}")));
        var problems = new List<string>();
        var warnings = new List<string>();
        var archs = slices.Select(s => s.Architecture).ToList();
        foreach (var want in expected.Architectures.Where(a => !archs.Contains(a))) problems.Add($"no {want} slice (the project's ARCHS ask for it)");
        foreach (var s in slices.Where(s => s.Platform is not null and not "iOS")) problems.Add($"{s.Architecture} slice is built for {s.Platform}, not iOS");
        var deploy = expected.DeploymentTarget;
        bool AtLeast14() => deploy is null || (Version.TryParse(deploy.Contains('.') ? deploy : deploy + ".0", out var d) && d >= new Version(14, 0));
        foreach (var s in slices.Where(s => s.IsArm64e && !s.PtrAuthVersioned))
            (AtLeast14() ? problems : warnings).Add("the arm64e slice uses the old (unversioned) pointer-authentication ABI, which arm64e processes on iOS 14 and later do not load — build with Xcode's clang (Apple toolchain), or drop arm64e from ARCHS after confirming the device");
        if (deploy is not null)
            foreach (var s in slices.Where(s => s.MinimumOS is not null && s.MinimumOS != deploy && !(s.MinimumOS.StartsWith(deploy, StringComparison.Ordinal))))
                warnings.Add($"{s.Architecture} slice records minimum iOS {s.MinimumOS}; the configuration's TARGET says {deploy}");
        var summary = $"/{rel}: {string.Join(", ", slices.Select(s => s.Architecture + (s.IsArm64e ? (s.PtrAuthVersioned ? $" (ptrauth v{s.PtrAuthAbiVersion})" : " (old ABI)") : "")))} · {slices[0].Type} · iOS {slices.Select(s => s.MinimumOS).FirstOrDefault(m => m is not null) ?? "?"}+";
        return problems.Count > 0 ? new($"{name} binary", VerificationStatus.Failed, summary + " — " + string.Join("; ", problems.Concat(warnings)))
            : warnings.Count > 0 ? new($"{name} binary", VerificationStatus.PassedWithWarnings, summary + " — " + string.Join("; ", warnings))
            : new($"{name} binary", VerificationStatus.Passed, summary);
    }

    /// <summary>The tweak's MobileSubstrate filter (binary/XML plist, or OpenStep text as Theos leaves it on Linux).</summary>
    internal static VerificationCheck FilterCheck(string name, string path)
    {
        var raw = File.ReadAllBytes(path);
        IReadOnlyList<string>? bundles = null;
        try
        {
            if (PlistReader.ParseDictionary(raw) is { } d && d.TryGetValue("Filter", out var f) && f is Dictionary<string, object> fd && fd.TryGetValue("Bundles", out var b) && b is List<object> list)
                bundles = list.Select(o => o.ToString() ?? "").ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or ArgumentException or InvalidCastException)
        {
            if (OpenStepFilterRegex().Match(Encoding.UTF8.GetString(raw)) is { Success: true } m)
                bundles = m.Groups["b"].Value.Split(',').Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).ToList();
        }
        return bundles is null
            ? new($"{name} filter", VerificationStatus.PassedWithWarnings, "The filter plist is present, but its Filter › Bundles could not be read.")
            : new($"{name} filter", bundles.Count > 0 ? VerificationStatus.Passed : VerificationStatus.Failed,
                bundles.Count > 0 ? "Injected into: " + string.Join(", ", bundles) : "The filter names no bundle: the tweak would not be loaded anywhere.");
    }

    internal static Dictionary<string, string> ParseControl(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (char.IsWhiteSpace(line[0]) && last is not null) { fields[last] += "\n" + line.Trim(); continue; }
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            last = line[..i].Trim();
            fields[last] = line[(i + 1)..].Trim();
        }
        return fields;
    }

    /// <summary>
    /// Extracts regular files and directories of a tar stream. Theos' dm.pl (like dpkg-deb) writes the archive root as an
    /// entry named "./", which TarFile.ExtractToDirectory rejects as "outside the destination" (CI run 74); it is skipped.
    /// Every other entry must stay inside the folder; links and devices are not reproduced (only their names are needed).
    /// </summary>
    internal static async Task ExtractTarAsync(Stream tar, string targetDir, CancellationToken ct)
    {
        var root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        await using var reader = new TarReader(tar);
        long total = 0;
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/');
            if (name.Length == 0 || name == ".") continue;
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"The package contains an entry outside its root: {entry.Name}");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    total += entry.Length;
                    if (total > MaxMemberBytes) throw new InvalidDataException($"The package payload is larger than {MaxMemberBytes:N0} bytes.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var output = File.Create(path))
                        if (entry.DataStream is { } data) await data.CopyToAsync(output, ct).ConfigureAwait(false);
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, "", ct).ConfigureAwait(false);   // recorded by name; never followed
                    break;
            }
        }
    }

    private static async Task ExtractAsync(string debPath, ArMember member, string targetDir, IProcessRunner runner, CancellationToken ct)
    {
        if (member.Size > MaxMemberBytes) throw new InvalidDataException($"{member.Name} is larger than {MaxMemberBytes:N0} bytes.");
        Directory.CreateDirectory(targetDir);
        await using var fs = File.OpenRead(debPath);
        fs.Position = member.Offset;
        var bounded = new BoundedStream(fs, member.Size);
        if (member.Name is "control.tar" or "data.tar" or "control.tar.gz" or "data.tar.gz")
        {
            Stream tar = member.Name.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(bounded, CompressionMode.Decompress) : bounded;
            await using (tar) await ExtractTarAsync(tar, targetDir, ct).ConfigureAwait(false);
            return;
        }
        // xz / lzma / zstd / bzip2: the system tar reads them. The member is copied to a private file first.
        var memberFile = Path.Combine(Path.GetDirectoryName(targetDir)!, member.Name);
        await using (var outFile = File.Create(memberFile)) await bounded.CopyToAsync(outFile, ct).ConfigureAwait(false);
        var compression = Path.GetExtension(member.Name) switch { ".lzma" => "--lzma", ".xz" => "--xz", ".zst" => "--zstd", ".bz2" => "--bzip2", _ => null }
                          ?? throw new InvalidDataException($"{member.Name}: unsupported compression.");
        var r = await runner.CaptureAsync(new ProcessSpec
        {
            Executable = new[] { "/usr/bin/tar", "/bin/tar" }.FirstOrDefault(File.Exists) ?? throw new InvalidDataException("tar was not found (/usr/bin/tar); it is needed to read this package's compressed payload."),
            Arguments = ["-x", compression, "-f", memberFile, "-C", targetDir],
            Timeout = TimeSpan.FromMinutes(5),
        }, ct).ConfigureAwait(false);
        if (!r.Result.Succeeded)
            throw new InvalidDataException($"{member.Name} could not be unpacked with tar {compression} (exit code {r.Result.ExitCode}): {r.StdErr.Trim()}" +
                                           (compression is "--lzma" or "--xz" ? " On Linux, install xz-utils (it provides lzma and xz)." : ""));
    }

    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, (int)Math.Min(count, length - _read));
            _read += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [GeneratedRegex(@"Bundles\s*=\s*\((?<b>[^)]*)\)")] private static partial Regex OpenStepFilterRegex();
}
