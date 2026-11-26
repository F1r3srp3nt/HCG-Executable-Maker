using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The desktop build half written for builds requested through <c>hcg.executable-maker.integration/1</c>
/// (docs/INTEGRATION-CONTRACT.md): valid against the pinned schema and Maker's build-half rules, and it never claims a
/// signature, a device result, an approval or a channel.
/// </summary>
public sealed class DesktopReleasePassportTests
{
    private static (BuildRecord Build, ArtifactRecord Artifact) Sample(string signatureDetail = "ad hoc signature (linker-signed)")
    {
        var id = Guid.NewGuid();
        var artifact = new ArtifactRecord
        {
            Id = Guid.NewGuid(), BuildId = id, Format = PackagingFormat.MacAppBundle, Kind = ArtifactKind.Directory,
            Path = "/out/HCG NoteDesk-1.0.0-osx-arm64/HCG NoteDesk.app", SizeBytes = 1234,
            Sha256 = new string('a', 64), Verification = VerificationStatus.Passed, CreatedAt = DateTimeOffset.UnixEpoch,
            Checks = [new VerificationCheck("Executable present", VerificationStatus.Passed, "HCG.NoteDesk.Host"), new VerificationCheck("Code signature", VerificationStatus.Passed, signatureDetail)],
        };
        var build = new BuildRecord
        {
            Id = id, ProjectId = Guid.NewGuid(), StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch.AddMinutes(1), Status = BuildStatus.Succeeded,
            Settings = new BuildSettings
            {
                ProjectRelativePath = "Host.csproj", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/out",
                Identity = new AppIdentity { ApplicationName = "HCG NoteDesk", ApplicationId = "com.hakmicapital.notedesk", Version = "1.0.0" },
                PackagingFormats = [PackagingFormat.MacAppBundle],
            },
            ToolchainVersions = new Dictionary<string, string> { [ToolchainIds.DotNetSdk] = "10.0.100" },
        };
        build.Artifacts.Add(artifact);
        return (build, artifact);
    }

    [Fact]
    public void The_desktop_build_half_is_a_valid_build_half()
    {
        var (build, artifact) = Sample();
        var passport = ReleasePassport.DesktopBuildHalf(build, artifact, "HCG Executable Maker test", "F1r3srp3nt/Example", new string('b', 40));
        Assert.Empty(ReleasePassport.ValidateBuildHalf(passport));
        Assert.Equal("macos", passport["platform"]!.GetValue<string>());
        Assert.Equal("arm64", passport["architecture"]!.GetValue<string>());
        Assert.Equal("executable-maker-local", passport["build"]!["provider"]!.GetValue<string>());
        Assert.Equal(".NET SDK 10.0.100", passport["build"]!["toolchain"]!.GetValue<string>());
    }

    [Fact]
    public void It_states_no_signing_method_device_result_approval_or_channel()
    {
        var (build, artifact) = Sample();
        var passport = ReleasePassport.DesktopBuildHalf(build, artifact, "test");
        Assert.Null(passport["code_signing"]!["method_claimed"]);
        Assert.Contains("ad hoc", passport["code_signing"]!["observed"]!.GetValue<string>());
        foreach (var level in new[] { "device_installation", "application_launch", "runtime_verification" })
            Assert.Equal("not-recorded", passport["validation"]!["levels"]![level]!["state"]!.GetValue<string>());
        Assert.Empty(passport["approvals"]!.AsArray());
        Assert.False(passport["distribution"]!.AsObject().ContainsKey("channel"));
    }
}
