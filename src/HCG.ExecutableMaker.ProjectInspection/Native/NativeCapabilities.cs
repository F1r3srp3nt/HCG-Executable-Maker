using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Native;

/// <summary>
/// What a C/C++ project needs on THIS computer, and whether it is there. HCG builds for the host platform with the
/// host's compilers only: a Mac produces macOS binaries, Linux produces Linux binaries, Windows produces Windows
/// binaries. Cross-compiling (Windows or iOS binaries on a Mac, …) needs a target toolchain HCG does not set up.
/// </summary>
public static class NativeCapabilities
{
    public sealed record Assessment(SupportLevel Support, string Summary, IReadOnlyList<string> Remediation, IReadOnlyList<ToolchainRequirement> Requirements);

    public static Assessment Evaluate(NativeProjectInfo project, ToolchainReport? report)
    {
        if (project.Kind == NativeProjectKind.Theos && project.UnsupportedReason is null && project.Theos is { } theos)
            return EvaluateTheos(project, theos.Schemes.FirstOrDefault(s => s.IsDefault && s.ProjectError is null) ?? theos.Schemes.FirstOrDefault(s => s.ProjectError is null), report);
        if (project.Kind is NativeProjectKind.Theos or NativeProjectKind.Unsupported)
            return new(SupportLevel.Unsupported, project.UnsupportedReason ?? "Not supported.", [], []);
        if (report is null)
            return new(SupportLevel.MissingToolchain, "Toolchains have not been detected yet.", ["Open the Toolchains page and press Refresh."], []);

        var requirements = new List<ToolchainRequirement>();
        var fixes = new List<string>();
        void Need(string id, string name, string why)
        {
            var row = report.Find(id);
            var ok = row?.IsAvailable == true;
            requirements.Add(new ToolchainRequirement(id, name, null, ok, ok ? $"{row!.ExecutablePath} {row.Version}".Trim() : row?.Detail ?? why));
            if (!ok) fixes.Add($"{name} is {(row?.Status == ToolchainStatus.IncompatibleVersion ? "too old" : "missing")}: {why} {row?.InstallHint ?? ""}".Trim());
        }

        var windows = report.Host.Platform == TargetPlatform.Windows;
        var languages = project.Languages.Count > 0 ? project.Languages : ["C", "CXX"];
        if (project.Kind is NativeProjectKind.CMake or NativeProjectKind.Sources)
        {
            Need(ToolchainIds.CMake, "CMake 3.20+", "it configures the project and reports its targets.");
            if (windows && report.IsAvailable(ToolchainIds.MsvcTools)) Need(ToolchainIds.MsvcTools, "Visual Studio C++ tools (MSVC)", "CMake's Visual Studio generator uses them.");
            else
            {
                if (languages.Contains("C")) Need(ToolchainIds.CCompiler, "C compiler", "the project compiles C.");
                if (languages.Contains("CXX")) Need(ToolchainIds.CxxCompiler, "C++ compiler", "the project compiles C++.");
                if (report.IsAvailable(ToolchainIds.Ninja)) Need(ToolchainIds.Ninja, "Ninja", "CMake drives the compiler with it.");
                else if (report.IsAvailable(ToolchainIds.Make)) Need(ToolchainIds.Make, "make", "CMake drives the compiler with it (Ninja was not found).");
                else
                {
                    requirements.Add(new ToolchainRequirement(ToolchainIds.Ninja, "Ninja or make", null, false, "CMake needs a build tool to drive the compiler."));
                    fixes.Add("Neither Ninja nor make was found: CMake needs one of them to build. " + (report.Find(ToolchainIds.Ninja)?.InstallHint ?? ""));
                }
            }
        }
        else if (project.Kind == NativeProjectKind.Make)
        {
            Need(ToolchainIds.Make, "make", "the project is built with its Makefile.");
            var anyCompiler = report.IsAvailable(ToolchainIds.CCompiler) || report.IsAvailable(ToolchainIds.CxxCompiler);
            requirements.Add(new ToolchainRequirement(ToolchainIds.CCompiler, "C/C++ compiler", null, anyCompiler,
                anyCompiler ? "The Makefile chooses its own compiler (CC/CXX); a host compiler is installed." : "No C or C++ compiler was found."));
            if (!anyCompiler) fixes.Add("No C or C++ compiler was found. " + (report.Find(ToolchainIds.CCompiler)?.InstallHint ?? ""));
        }

        if (fixes.Count > 0)
            return new(SupportLevel.MissingToolchain, $"{project.Name}: this computer is missing tools the {KindLabel(project.Kind)} needs.", fixes, requirements);
        return new(SupportLevel.Supported, $"{KindLabel(project.Kind)} — builds for {report.Host.Platform.DisplayName()} {report.Host.Architecture.DisplayName()} with this computer's toolchain.", [], requirements);
    }

    /// <summary>
    /// What a Theos package build needs for one of the project's configurations: Theos, GNU make 4+, ldid, perl, an
    /// iOS-targeting clang, and the SDK the configuration's TARGET names. Each missing item is reported on its own.
    /// </summary>
    public static Assessment EvaluateTheos(NativeProjectInfo project, TheosScheme? scheme, ToolchainReport? report)
    {
        if (scheme is null)
            return new(SupportLevel.RequiresConfiguration, $"{project.Name}: the Makefile defines no package configuration HCG can build (every branch stops with the project's own error).", [], []);
        if (scheme.ProjectError is { } projectError)
            return new(SupportLevel.RequiresConfiguration, $"The project refuses {scheme.Label}: {projectError}", [], []);
        if (report is null) return new(SupportLevel.MissingToolchain, "Toolchains have not been detected yet.", ["Open the Toolchains page and press Refresh."], []);
        if (report.Host.Platform == TargetPlatform.Windows)
            return new(SupportLevel.RequiresDifferentHost, "Theos packages are built on macOS or Linux.", ["Build on a Mac (recommended: Xcode's clang produces the current arm64e ABI) or on Linux."], []);
        var requirements = new List<ToolchainRequirement>();
        var fixes = new List<string>();
        void Need(string id, string name, string why)
        {
            var row = report.Find(id);
            var ok = row?.IsAvailable == true;
            requirements.Add(new ToolchainRequirement(id, name, null, ok, ok ? $"{row!.ExecutablePath} {row.Version}".Trim() : row?.Detail ?? why));
            if (!ok) fixes.Add($"{name}: {(row?.Detail is { Length: > 0 } d ? d : why)} {row?.InstallHint ?? ""}".Trim());
        }
        Need(ToolchainIds.Theos, "Theos", "Theos' makefiles build and package the project.");
        Need(ToolchainIds.TheosMake, "GNU make 4+", "Theos' makefiles need GNU make 4 or later.");
        Need(ToolchainIds.TheosIosToolchain, "iOS toolchain (clang for iphoneos)", "compiles the project for iOS.");
        Need(ToolchainIds.Ldid, "ldid", "signs the built binaries.");
        Need(ToolchainIds.Perl, "perl", "runs Logos and the package builder.");
        if (report.Find(ToolchainIds.Theos) is { IsAvailable: true } theosRow && scheme.SdkVersion is { } sdk)
        {
            var inTheos = theosRow.AdditionalVersions.Contains("iPhoneOS" + sdk + ".sdk");
            requirements.Add(new ToolchainRequirement(ToolchainIds.Theos + "-sdk", $"iPhoneOS {sdk} SDK", null, inTheos,
                inTheos ? $"{theosRow.ExecutablePath}/sdks/iPhoneOS{sdk}.sdk" : $"$THEOS/sdks has {(theosRow.AdditionalVersions.Count == 0 ? "no SDKs" : string.Join(", ", theosRow.AdditionalVersions))}"));
            if (!inTheos)
                fixes.Add($"iPhoneOS {sdk} SDK: {scheme.Label} builds with TARGET iphone:clang:{sdk}:{scheme.DeploymentTarget}, so Theos needs iPhoneOS{sdk}.sdk in {theosRow.ExecutablePath}/sdks. " +
                          "Copy that SDK there yourself (for example from github.com/theos/sdks). HCG does not download SDKs or change the project's TARGET.");
        }
        if (fixes.Count > 0)
            return new(SupportLevel.MissingToolchain, $"{project.Name} ({scheme.Label}): this computer is missing what the Theos build needs.", fixes, requirements);
        return new(SupportLevel.Supported, $"Theos package — {scheme.Label}: iOS {scheme.DeploymentTarget ?? "(Theos default)"}+, SDK {scheme.SdkVersion ?? "latest"}, " +
            $"installs under {scheme.InstallPrefix}. Built on this {report.Host.Platform.DisplayName()} computer into a .deb for jailbroken devices.", [], requirements);
    }

    public static string KindLabel(NativeProjectKind kind) => kind switch
    {
        NativeProjectKind.CMake => "CMake project",
        NativeProjectKind.Make => "Makefile project",
        NativeProjectKind.Sources => "C/C++ sources without a build system",
        NativeProjectKind.Theos => "Theos project (jailbreak package)",
        _ => "C/C++ project (build system not supported)",
    };
}
