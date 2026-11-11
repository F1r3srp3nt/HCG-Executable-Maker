using System.Security.Cryptography;
using System.Security;
using System.Text;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>
/// Writes a minimal, standard iOS app Xcode project (project.pbxproj + shared scheme): one application target that
/// compiles the given Swift files and links library products of a local Swift package. Object identifiers are
/// deterministic so regenerating produces the same file.
/// </summary>
internal static class XcodeProjectWriter
{
    public sealed record Spec
    {
        public required string TargetName { get; init; }
        public required string DisplayName { get; init; }
        public required string BundleId { get; init; }
        public required string Version { get; init; }
        public required int BuildNumber { get; init; }
        public required string DeploymentTarget { get; init; }
        /// <summary>Folder of the app's sources relative to the .xcodeproj's parent folder, e.g. "../Sources/RelayApp".</summary>
        public required string SourceFolder { get; init; }
        /// <summary>Swift files relative to <see cref="SourceFolder"/>.</summary>
        public required IReadOnlyList<string> SwiftFiles { get; init; }
        /// <summary>Local package folder relative to the .xcodeproj's parent folder (null: no package).</summary>
        public string? LocalPackagePath { get; init; }
        public IReadOnlyList<string> PackageProducts { get; init; } = [];
        public IReadOnlyDictionary<string, string> InfoPlistKeys { get; init; } = new Dictionary<string, string>();
    }

    private static string Id(string key) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("hcg-port:" + key)))[..24];

    /// <summary>OpenStep string: bare when safe, otherwise quoted with escapes.</summary>
    internal static string Q(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '$') && !s.Contains("//", StringComparison.Ordinal)
            ? s
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    public static string Pbxproj(Spec s)
    {
        var t = s.TargetName;
        string project = Id("project"), target = Id("target"), mainGroup = Id("group:main"), productsGroup = Id("group:products"),
            sourcesGroup = Id("group:sources"), product = Id("product"), sourcesPhase = Id("phase:sources"),
            frameworksPhase = Id("phase:frameworks"), resourcesPhase = Id("phase:resources"),
            projectConfigs = Id("configs:project"), targetConfigs = Id("configs:target"),
            projDebug = Id("config:project:Debug"), projRelease = Id("config:project:Release"),
            tgtDebug = Id("config:target:Debug"), tgtRelease = Id("config:target:Release"), package = Id("package:local");

        var sb = new StringBuilder();
        void L(string line) => sb.Append(line).Append('\n');
        L("// !$*UTF8*$!");
        L("{");
        L("\tarchiveVersion = 1;");
        L("\tclasses = {");
        L("\t};");
        L("\tobjectVersion = 60;");
        L("\tobjects = {");
        L("");
        L("/* Begin PBXBuildFile section */");
        foreach (var f in s.SwiftFiles)
            L($"\t\t{Id("build:" + f)} /* {Path.GetFileName(f)} in Sources */ = {{isa = PBXBuildFile; fileRef = {Id("file:" + f)} /* {Path.GetFileName(f)} */; }};");
        foreach (var p in s.PackageProducts)
            L($"\t\t{Id("build:product:" + p)} /* {p} in Frameworks */ = {{isa = PBXBuildFile; productRef = {Id("dep:" + p)} /* {p} */; }};");
        L("/* End PBXBuildFile section */");
        L("");
        L("/* Begin PBXFileReference section */");
        L($"\t\t{product} /* {t}.app */ = {{isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = {Q(t + ".app")}; sourceTree = BUILT_PRODUCTS_DIR; }};");
        foreach (var f in s.SwiftFiles)
            L($"\t\t{Id("file:" + f)} /* {Path.GetFileName(f)} */ = {{isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {Q(f)}; sourceTree = \"<group>\"; }};");
        L("/* End PBXFileReference section */");
        L("");
        L("/* Begin PBXFrameworksBuildPhase section */");
        L($"\t\t{frameworksPhase} /* Frameworks */ = {{");
        L("\t\t\tisa = PBXFrameworksBuildPhase;");
        L("\t\t\tbuildActionMask = 2147483647;");
        L("\t\t\tfiles = (");
        foreach (var p in s.PackageProducts) L($"\t\t\t\t{Id("build:product:" + p)} /* {p} in Frameworks */,");
        L("\t\t\t);");
        L("\t\t\trunOnlyForDeploymentPostprocessing = 0;");
        L("\t\t};");
        L("/* End PBXFrameworksBuildPhase section */");
        L("");
        L("/* Begin PBXGroup section */");
        L($"\t\t{mainGroup} = {{");
        L("\t\t\tisa = PBXGroup;");
        L("\t\t\tchildren = (");
        L($"\t\t\t\t{sourcesGroup} /* {t} */,");
        L($"\t\t\t\t{productsGroup} /* Products */,");
        L("\t\t\t);");
        L("\t\t\tsourceTree = \"<group>\";");
        L("\t\t};");
        L($"\t\t{productsGroup} /* Products */ = {{");
        L("\t\t\tisa = PBXGroup;");
        L("\t\t\tchildren = (");
        L($"\t\t\t\t{product} /* {t}.app */,");
        L("\t\t\t);");
        L("\t\t\tname = Products;");
        L("\t\t\tsourceTree = \"<group>\";");
        L("\t\t};");
        L($"\t\t{sourcesGroup} /* {t} */ = {{");
        L("\t\t\tisa = PBXGroup;");
        L("\t\t\tchildren = (");
        foreach (var f in s.SwiftFiles) L($"\t\t\t\t{Id("file:" + f)} /* {Path.GetFileName(f)} */,");
        L("\t\t\t);");
        L($"\t\t\tname = {Q(t)};");
        L($"\t\t\tpath = {Q(s.SourceFolder)};");
        L("\t\t\tsourceTree = \"<group>\";");
        L("\t\t};");
        L("/* End PBXGroup section */");
        L("");
        L("/* Begin PBXNativeTarget section */");
        L($"\t\t{target} /* {t} */ = {{");
        L("\t\t\tisa = PBXNativeTarget;");
        L($"\t\t\tbuildConfigurationList = {targetConfigs} /* Build configuration list for PBXNativeTarget \"{t}\" */;");
        L("\t\t\tbuildPhases = (");
        L($"\t\t\t\t{sourcesPhase} /* Sources */,");
        L($"\t\t\t\t{frameworksPhase} /* Frameworks */,");
        L($"\t\t\t\t{resourcesPhase} /* Resources */,");
        L("\t\t\t);");
        L("\t\t\tbuildRules = (");
        L("\t\t\t);");
        L("\t\t\tdependencies = (");
        L("\t\t\t);");
        L($"\t\t\tname = {Q(t)};");
        L("\t\t\tpackageProductDependencies = (");
        foreach (var p in s.PackageProducts) L($"\t\t\t\t{Id("dep:" + p)} /* {p} */,");
        L("\t\t\t);");
        L($"\t\t\tproductName = {Q(t)};");
        L($"\t\t\tproductReference = {product} /* {t}.app */;");
        L("\t\t\tproductType = \"com.apple.product-type.application\";");
        L("\t\t};");
        L("/* End PBXNativeTarget section */");
        L("");
        L("/* Begin PBXProject section */");
        L($"\t\t{project} /* Project object */ = {{");
        L("\t\t\tisa = PBXProject;");
        L("\t\t\tattributes = {");
        L("\t\t\t\tBuildIndependentTargetsInParallel = 1;");
        L("\t\t\t\tLastSwiftUpdateCheck = 1500;");
        L("\t\t\t\tLastUpgradeCheck = 1500;");
        L("\t\t\t};");
        L($"\t\t\tbuildConfigurationList = {projectConfigs} /* Build configuration list for PBXProject \"{t}\" */;");
        L("\t\t\tcompatibilityVersion = \"Xcode 14.0\";");
        L("\t\t\tdevelopmentRegion = en;");
        L("\t\t\thasScannedForEncodings = 0;");
        L("\t\t\tknownRegions = (");
        L("\t\t\t\ten,");
        L("\t\t\t\tBase,");
        L("\t\t\t);");
        L($"\t\t\tmainGroup = {mainGroup};");
        if (s.LocalPackagePath is not null)
        {
            L("\t\t\tpackageReferences = (");
            L($"\t\t\t\t{package} /* XCLocalSwiftPackageReference \"{s.LocalPackagePath}\" */,");
            L("\t\t\t);");
        }
        L($"\t\t\tproductRefGroup = {productsGroup} /* Products */;");
        L("\t\t\tprojectDirPath = \"\";");
        L("\t\t\tprojectRoot = \"\";");
        L("\t\t\ttargets = (");
        L($"\t\t\t\t{target} /* {t} */,");
        L("\t\t\t);");
        L("\t\t};");
        L("/* End PBXProject section */");
        L("");
        L("/* Begin PBXResourcesBuildPhase section */");
        L($"\t\t{resourcesPhase} /* Resources */ = {{");
        L("\t\t\tisa = PBXResourcesBuildPhase;");
        L("\t\t\tbuildActionMask = 2147483647;");
        L("\t\t\tfiles = (");
        L("\t\t\t);");
        L("\t\t\trunOnlyForDeploymentPostprocessing = 0;");
        L("\t\t};");
        L("/* End PBXResourcesBuildPhase section */");
        L("");
        L("/* Begin PBXSourcesBuildPhase section */");
        L($"\t\t{sourcesPhase} /* Sources */ = {{");
        L("\t\t\tisa = PBXSourcesBuildPhase;");
        L("\t\t\tbuildActionMask = 2147483647;");
        L("\t\t\tfiles = (");
        foreach (var f in s.SwiftFiles) L($"\t\t\t\t{Id("build:" + f)} /* {Path.GetFileName(f)} in Sources */,");
        L("\t\t\t);");
        L("\t\t\trunOnlyForDeploymentPostprocessing = 0;");
        L("\t\t};");
        L("/* End PBXSourcesBuildPhase section */");
        L("");
        L("/* Begin XCBuildConfiguration section */");
        void ProjectConfig(string id, bool debug)
        {
            L($"\t\t{id} /* {(debug ? "Debug" : "Release")} */ = {{");
            L("\t\t\tisa = XCBuildConfiguration;");
            L("\t\t\tbuildSettings = {");
            L("\t\t\t\tALWAYS_SEARCH_USER_PATHS = NO;");
            L("\t\t\t\tCLANG_ENABLE_MODULES = YES;");
            L("\t\t\t\tCLANG_ENABLE_OBJC_ARC = YES;");
            L($"\t\t\t\tDEBUG_INFORMATION_FORMAT = {(debug ? "dwarf" : "\"dwarf-with-dsym\"")};");
            L("\t\t\t\tENABLE_STRICT_OBJC_MSGSEND = YES;");
            if (debug) L("\t\t\t\tENABLE_TESTABILITY = YES;");
            L("\t\t\t\tGCC_C_LANGUAGE_STANDARD = gnu17;");
            L($"\t\t\t\tIPHONEOS_DEPLOYMENT_TARGET = {Q(s.DeploymentTarget)};");
            if (debug) L("\t\t\t\tONLY_ACTIVE_ARCH = YES;");
            L("\t\t\t\tSDKROOT = iphoneos;");
            if (debug) L("\t\t\t\tSWIFT_ACTIVE_COMPILATION_CONDITIONS = \"DEBUG $(inherited)\";");
            L($"\t\t\t\tSWIFT_OPTIMIZATION_LEVEL = {(debug ? "\"-Onone\"" : "\"-O\"")};");
            if (!debug) L("\t\t\t\tSWIFT_COMPILATION_MODE = wholemodule;");
            if (!debug) L("\t\t\t\tVALIDATE_PRODUCT = YES;");
            L("\t\t\t};");
            L($"\t\t\tname = {(debug ? "Debug" : "Release")};");
            L("\t\t};");
        }
        void TargetConfig(string id, bool debug)
        {
            L($"\t\t{id} /* {(debug ? "Debug" : "Release")} */ = {{");
            L("\t\t\tisa = XCBuildConfiguration;");
            L("\t\t\tbuildSettings = {");
            L("\t\t\t\tCODE_SIGN_STYLE = Automatic;");
            L($"\t\t\t\tCURRENT_PROJECT_VERSION = {s.BuildNumber};");
            L("\t\t\t\tENABLE_PREVIEWS = YES;");
            L("\t\t\t\tGENERATE_INFOPLIST_FILE = YES;");
            L($"\t\t\t\tINFOPLIST_KEY_CFBundleDisplayName = {Q(s.DisplayName)};");
            foreach (var (k, v) in s.InfoPlistKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
                L($"\t\t\t\tINFOPLIST_KEY_{k} = {Q(v)};");
            L("\t\t\t\tINFOPLIST_KEY_UIApplicationSceneManifest_Generation = YES;");
            L("\t\t\t\tINFOPLIST_KEY_UIApplicationSupportsIndirectInputEvents = YES;");
            L("\t\t\t\tINFOPLIST_KEY_UILaunchScreen_Generation = YES;");
            L("\t\t\t\tINFOPLIST_KEY_UISupportedInterfaceOrientations_iPad = \"UIInterfaceOrientationPortrait UIInterfaceOrientationPortraitUpsideDown UIInterfaceOrientationLandscapeLeft UIInterfaceOrientationLandscapeRight\";");
            L("\t\t\t\tINFOPLIST_KEY_UISupportedInterfaceOrientations_iPhone = \"UIInterfaceOrientationPortrait UIInterfaceOrientationLandscapeLeft UIInterfaceOrientationLandscapeRight\";");
            L("\t\t\t\tLD_RUNPATH_SEARCH_PATHS = (");
            L("\t\t\t\t\t\"$(inherited)\",");
            L("\t\t\t\t\t\"@executable_path/Frameworks\",");
            L("\t\t\t\t);");
            L($"\t\t\t\tMARKETING_VERSION = {Q(s.Version)};");
            L($"\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = {Q(s.BundleId)};");
            L("\t\t\t\tPRODUCT_NAME = \"$(TARGET_NAME)\";");
            L("\t\t\t\tSUPPORTED_PLATFORMS = \"iphoneos iphonesimulator\";");
            L("\t\t\t\tSWIFT_EMIT_LOC_STRINGS = YES;");
            L("\t\t\t\tSWIFT_VERSION = 5.0;");
            L("\t\t\t\tTARGETED_DEVICE_FAMILY = \"1,2\";");
            L("\t\t\t};");
            L($"\t\t\tname = {(debug ? "Debug" : "Release")};");
            L("\t\t};");
        }
        ProjectConfig(projDebug, true);
        ProjectConfig(projRelease, false);
        TargetConfig(tgtDebug, true);
        TargetConfig(tgtRelease, false);
        L("/* End XCBuildConfiguration section */");
        L("");
        L("/* Begin XCConfigurationList section */");
        foreach (var (id, kind, d, r) in new[] { (projectConfigs, $"PBXProject \"{t}\"", projDebug, projRelease), (targetConfigs, $"PBXNativeTarget \"{t}\"", tgtDebug, tgtRelease) })
        {
            L($"\t\t{id} /* Build configuration list for {kind} */ = {{");
            L("\t\t\tisa = XCConfigurationList;");
            L("\t\t\tbuildConfigurations = (");
            L($"\t\t\t\t{d} /* Debug */,");
            L($"\t\t\t\t{r} /* Release */,");
            L("\t\t\t);");
            L("\t\t\tdefaultConfigurationIsVisible = 0;");
            L("\t\t\tdefaultConfigurationName = Release;");
            L("\t\t};");
        }
        L("/* End XCConfigurationList section */");
        if (s.LocalPackagePath is not null)
        {
            L("");
            L("/* Begin XCLocalSwiftPackageReference section */");
            L($"\t\t{package} /* XCLocalSwiftPackageReference \"{s.LocalPackagePath}\" */ = {{");
            L("\t\t\tisa = XCLocalSwiftPackageReference;");
            L($"\t\t\trelativePath = {Q(s.LocalPackagePath)};");
            L("\t\t};");
            L("/* End XCLocalSwiftPackageReference section */");
        }
        if (s.PackageProducts.Count > 0)
        {
            L("");
            L("/* Begin XCSwiftPackageProductDependency section */");
            foreach (var p in s.PackageProducts)
            {
                L($"\t\t{Id("dep:" + p)} /* {p} */ = {{");
                L("\t\t\tisa = XCSwiftPackageProductDependency;");
                if (s.LocalPackagePath is not null) L($"\t\t\tpackage = {package} /* XCLocalSwiftPackageReference \"{s.LocalPackagePath}\" */;");
                L($"\t\t\tproductName = {Q(p)};");
                L("\t\t};");
            }
            L("/* End XCSwiftPackageProductDependency section */");
        }
        L("\t};");
        L($"\trootObject = {project} /* Project object */;");
        L("}");
        return sb.ToString();
    }

    public static string Scheme(Spec s)
    {
        var target = Id("target");
        var name = SecurityElement.Escape(s.TargetName);
        var reference = $"""
                  <BuildableReference
                     BuildableIdentifier = "primary"
                     BlueprintIdentifier = "{target}"
                     BuildableName = "{name}.app"
                     BlueprintName = "{name}"
                     ReferencedContainer = "container:{name}.xcodeproj">
                  </BuildableReference>
            """;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Scheme LastUpgradeVersion = "1500" version = "1.7">
               <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
                  <BuildActionEntries>
                     <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            {reference}
                     </BuildActionEntry>
                  </BuildActionEntries>
               </BuildAction>
               <TestAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv = "YES">
                  <Testables>
                  </Testables>
               </TestAction>
               <LaunchAction buildConfiguration = "Debug" selectedDebuggerIdentifier = "Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier = "Xcode.DebuggerFoundation.Launcher.LLDB" launchStyle = "0" useCustomWorkingDirectory = "NO" ignoresPersistentStateOnLaunch = "NO" debugDocumentVersioning = "YES" debugServiceExtension = "internal" allowLocationSimulation = "YES">
                  <BuildableProductRunnable runnableDebuggingMode = "0">
            {reference}
                  </BuildableProductRunnable>
               </LaunchAction>
               <ProfileAction buildConfiguration = "Release" shouldUseLaunchSchemeArgsEnv = "YES" savedToolIdentifier = "" useCustomWorkingDirectory = "NO" debugDocumentVersioning = "YES">
                  <BuildableProductRunnable runnableDebuggingMode = "0">
            {reference}
                  </BuildableProductRunnable>
               </ProfileAction>
               <AnalyzeAction buildConfiguration = "Debug">
               </AnalyzeAction>
               <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES">
               </ArchiveAction>
            </Scheme>

            """;
    }
}
