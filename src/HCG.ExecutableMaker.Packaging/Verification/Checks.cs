using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Verification;

public static class Checks
{
    public static VerificationStatus Combine(IEnumerable<VerificationCheck> checks)
    {
        var list = checks.ToList();
        if (list.Count == 0) return VerificationStatus.NotVerifiable;
        if (list.Any(c => c.Status == VerificationStatus.Failed)) return VerificationStatus.Failed;
        if (list.Any(c => c.Status is VerificationStatus.PassedWithWarnings or VerificationStatus.NotVerifiable)) return VerificationStatus.PassedWithWarnings;
        return VerificationStatus.Passed;
    }

    public static VerificationCheck ExecutablePermission(string path, TargetPlatform target)
    {
        if (target == TargetPlatform.Windows)
            return new VerificationCheck("Executable permission", VerificationStatus.Passed, "Not required for Windows executables.");
        if (OperatingSystem.IsWindows())
            return new VerificationCheck("Executable permission", VerificationStatus.NotVerifiable,
                "This Windows host cannot store Unix permissions on disk. Use the ZIP artifact, which records them.");
        var mode = File.GetUnixFileMode(path);
        return (mode & UnixFileMode.UserExecute) != 0
            ? new VerificationCheck("Executable permission", VerificationStatus.Passed, "Executable bit is set.")
            : new VerificationCheck("Executable permission", VerificationStatus.Failed, "Executable bit is missing.");
    }

    public static VerificationCheck LaunchNotPerformed() => new("Launch test", VerificationStatus.NotVerifiable,
        "Not performed: launching would execute the project's code. Verify the app manually on a target machine.");
}
