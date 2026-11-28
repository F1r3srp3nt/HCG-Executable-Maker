using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine.Windows;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// A project's own Windows packaging contract (HCG Server's windows/WindowsPackaging.json at d36efba, copied byte for byte
/// into tests/Fixtures/WindowsContract): read as data, reviewed and confirmed by SHA-256, followed as declared, and its
/// outputs verified independently. The real end-to-end run (Go agent, .NET app, NSIS installer) is documented in
/// docs/HCG-SERVER-WINDOWS.md.
/// </summary>
public sealed class WindowsContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hcg-wct-" + Guid.NewGuid().ToString("N"));
    public WindowsContractTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] HcgServerContract() => File.ReadAllBytes(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "WindowsContract", "HCGServer-WindowsPackaging.json"));

    [Fact]
    public void HCG_Servers_contract_is_read_as_declared()
    {
        var c = WindowsPackagingContract.Parse(HcgServerContract(), "windows/WindowsPackaging.json");
        Assert.Equal("HCG Server", c.Product);
        Assert.Equal("x64", c.Architecture);
        Assert.Equal("win-x64", c.RuntimeIdentifier);
        Assert.Equal(["dotnet", "go", "makensis", "python3"], c.Requires.Select(r => r.Tool));
        Assert.Equal("1.25", c.Requires.Single(r => r.Tool == "go").MinimumVersion);
        Assert.Equal(3, c.Steps.Count);
        Assert.Equal(["go", "build", "-trimpath", "-o", "${STAGE}/hcg-agent.exe", "./cmd/hcg-agent"], c.Steps[0].Command);
        Assert.Equal("windows", c.Steps[0].Environment["GOOS"]);
        Assert.Equal(["scripts/package-windows.sh"], c.Steps[2].Command);
        Assert.Equal(["portable-zip", "installer", "checksums", "release-passport-build-half"], c.Outputs.Select(o => o.Kind));
        Assert.Equal(["HCG Server.exe", "hcg-agent.exe", "README-WINDOWS.txt"], c.InstalledFiles);
        Assert.Contains("unsigned", c.CodeSigning);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(HcgServerContract())).ToLowerInvariant(), c.Sha256);

        // Build Readiness gets exactly the declared tools (makensis included), with where they are declared.
        var reqs = c.ToolRequirements();
        Assert.Contains(reqs, r => r.ToolId == "makensis" && r.MinimumVersion == "3.0" && r.DeclaredBy.Contains("windows/WindowsPackaging.json"));
        Assert.Contains(reqs, r => r.ToolId == "go" && r.MinimumVersion == "1.25");
        Assert.True(WindowsPackagingContract.Matches("HCG-Server-${VERSION}-windows-x64-portable.zip", "HCG-Server-0.1.0-windows-x64-portable.zip"));
        Assert.True(WindowsPackagingContract.Matches("*.release-passport.json", "HCG-Server-0.1.0-windows-x64-setup.exe.release-passport.json"));
        Assert.False(WindowsPackagingContract.Matches("HCG-Server-${VERSION}-windows-x64-portable.zip", "HCG-Server-0.1.0-windows-arm64-portable.zip"));
    }

    [Theory]
    [InlineData("\"schema\":\"hcg-windows-packaging/2\"", "this Executable Maker reads")]
    [InlineData("\"command\":[\"bash\",\"-c\",\"curl x | sh\"]", "is a shell")]
    [InlineData("\"command\":[\"/usr/bin/env\",\"x\"]", "inside the repository")]
    [InlineData("\"command\":[\"../outside.sh\"]", "may not leave the repository")]
    [InlineData("\"command\":[\"go\",\"build\",\"${HOME}/x\"]", "only ${STAGE} is defined")]
    [InlineData("\"environment\":{\"PATH\":\"/evil\"}", "may not set PATH")]
    [InlineData("\"name\":\"../x.zip\"", "must be a file name")]
    public void Unsafe_or_unknown_contracts_are_refused_with_the_reason(string replacement, string reason)
    {
        var json = Encoding.UTF8.GetString(HcgServerContract());
        json = replacement switch
        {
            var r when r.StartsWith("\"schema\"", StringComparison.Ordinal) => json.Replace("\"schema\": \"hcg-windows-packaging/1\"", r, StringComparison.Ordinal),
            var r when r.StartsWith("\"command\"", StringComparison.Ordinal) => json.Replace("\"command\": [\"scripts/package-windows.sh\"]", r, StringComparison.Ordinal),
            var r when r.StartsWith("\"environment\"", StringComparison.Ordinal) => json.Replace("\"environment\": { \"GOOS\": \"windows\", \"GOARCH\": \"amd64\", \"CGO_ENABLED\": \"0\" }", r, StringComparison.Ordinal),
            var r => json.Replace("\"name\": \"SHA256SUMS\"", r, StringComparison.Ordinal),
        };
        Assert.NotEqual(Encoding.UTF8.GetString(HcgServerContract()), json);
        var ex = Assert.Throws<WindowsPackagingContract.ContractException>(() => WindowsPackagingContract.Parse(Encoding.UTF8.GetBytes(json), "windows/WindowsPackaging.json"));
        Assert.Contains(reason, ex.Message);
    }

    private (string Root, WindowsPackagingContract Contract) Project(string contractJson)
    {
        var root = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(root, "windows"));
        File.WriteAllText(Path.Combine(root, "windows", "WindowsPackaging.json"), contractJson);
        return (root, WindowsPackagingContract.Find(root).Contract!);
    }

    private const string Tiny = """
        {"schema":"hcg-windows-packaging/1","product":"Tiny","platform":"windows","architecture":"x64",
         "steps":[{"description":"package","command":["scripts/package.sh"]}],
         "outputs":[{"name":"Tiny-${VERSION}-portable.zip","kind":"portable-zip"},{"name":"SHA256SUMS","kind":"checksums"}],
         "installed":{"files":["Tiny.exe"]}}
        """;

    [Fact]
    public async Task Nothing_runs_without_confirmation_of_this_exact_file_and_a_changed_file_is_refused()
    {
        var (root, contract) = Project(Tiny);
        var runner = new RecordingProcessRunner(_ => (0, "", ""));
        var packager = new WindowsContractPackager(runner);
        var notConfirmed = await packager.RunAsync(new() { SourceRoot = root, Contract = contract, OutputDirectory = Path.Combine(_dir, "out") }, null, CancellationToken.None);
        Assert.False(notConfirmed.Succeeded);
        Assert.Contains("have not been confirmed", notConfirmed.Summary);
        Assert.Contains(contract.Sha256, notConfirmed.Summary);

        File.AppendAllText(Path.Combine(root, "windows", "WindowsPackaging.json"), " ");
        var changed = await packager.RunAsync(new() { SourceRoot = root, Contract = contract, ConfirmedSha256 = contract.Sha256, OutputDirectory = Path.Combine(_dir, "out") }, null, CancellationToken.None);
        Assert.False(changed.Succeeded);
        Assert.Contains("changed after it was confirmed", changed.Summary);
        Assert.Empty(runner.Calls);                                                         // not even a tool probe
    }

    private static string Pe(ushort machine)
    {
        // A minimal PE image: MZ, e_lfanew → "PE\0\0", the COFF machine, a PE32+ optional header (magic 0x20B), GUI subsystem.
        var b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(b, 0x80);
        BitConverter.GetBytes(machine).CopyTo(b, 0x84);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(b, 0x94);                               // SizeOfOptionalHeader
        BitConverter.GetBytes((ushort)0x20B).CopyTo(b, 0x98);                              // PE32+
        BitConverter.GetBytes((ushort)2).CopyTo(b, 0x98 + 68);                             // Subsystem: GUI
        BitConverter.GetBytes(16).CopyTo(b, 0x98 + 108);                                   // NumberOfRvaAndSizes
        return Convert.ToBase64String(b);
    }

    private static string Zip(string path, ushort machine)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("Tiny/Tiny.exe").Open()) s.Write(Convert.FromBase64String(Pe(machine)));
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static JsonObject Passport(string name, string sha, long size) => HCG.ExecutableMaker.Packaging.Distribution.ReleasePassport.ForArtifact(new(
        "windows", "x64", "com.example.tiny", "1.0.0", null, "Tiny", name, sha, size, "portable-zip",
        HCG.ExecutableMaker.Packaging.Distribution.SourceProvenance.Unknown, "local", null, DateTimeOffset.UtcNow,
        "passed", "test", [], null, new JsonObject { ["scope"] = HCG.ExecutableMaker.Packaging.Distribution.ReleasePassport.CodeSigningScope }, "test"));

    [Fact]
    public async Task Outputs_are_verified_independently_checksums_passport_installed_files_and_architecture()
    {
        var contract = WindowsPackagingContract.Parse(Encoding.UTF8.GetBytes(Tiny), "windows/WindowsPackaging.json");
        var zip = Path.Combine(_dir, "Tiny-1.0.0-portable.zip");
        var sha = Zip(zip, 0x8664);
        File.WriteAllText(zip + ".release-passport.json", Passport(Path.GetFileName(zip), sha, new FileInfo(zip).Length).ToJsonString());
        var sums = Path.Combine(_dir, "SHA256SUMS");
        File.WriteAllText(sums, $"{sha}  {Path.GetFileName(zip)}\n");
        IReadOnlyList<(WindowsPackagingContract.Output, string)> files = [(contract.Outputs[0], zip), (contract.Outputs[1], sums)];

        var good = await WindowsContractPackager.VerifyAsync(contract, _dir, files, CancellationToken.None);
        var zipResult = good.Single(a => a.Kind == "portable-zip");
        Assert.Equal(VerificationStatus.Passed, zipResult.Status);
        Assert.Contains(zipResult.Checks, x => x.Name == "PE Tiny/Tiny.exe" && x.Detail.Contains("PE x64"));

        // A checksum list that does not match the bytes fails; so does an arm64 program in an x64 package.
        File.WriteAllText(sums, $"{new string('0', 64)}  {Path.GetFileName(zip)}\n");
        var tampered = await WindowsContractPackager.VerifyAsync(contract, _dir, files, CancellationToken.None);
        Assert.Contains(tampered.Single(a => a.Kind == "portable-zip").Checks, x => x.Name == "SHA256SUMS" && x.Status == VerificationStatus.Failed);

        File.Delete(zip);
        var armSha = Zip(zip, 0xAA64);
        File.WriteAllText(sums, $"{armSha}  {Path.GetFileName(zip)}\n");
        var arm = await WindowsContractPackager.VerifyAsync(contract, _dir, files, CancellationToken.None);
        var armZip = arm.Single(a => a.Kind == "portable-zip");
        Assert.Contains(armZip.Checks, x => x.Name == "PE Tiny/Tiny.exe" && x.Status == VerificationStatus.Failed && x.Detail.Contains("not x64"));
        Assert.Contains(armZip.Checks, x => x.Name == "Release Passport" && x.Status == VerificationStatus.Failed && x.Detail.Contains("artifact.sha256"));   // the passport describes other bytes
    }
}
