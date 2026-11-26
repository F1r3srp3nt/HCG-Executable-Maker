using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// The versioned integration contract <c>hcg.executable-maker.integration/1</c> (docs/INTEGRATION-CONTRACT.md): lets
/// another HCG application on the same computer (HCG Executable Maker Unleashed first) discover what this installed
/// Executable Maker can build and submit a structured build request, without touching Executable Maker's internal files.
/// Builds go through the same <see cref="BuildService"/>, import, inspection, packaging and verification as the UI.
/// </summary>
/// <remarks>
/// <c>--integration describe</c> prints one JSON document. <c>--integration build --request F [--result F]</c> prints
/// JSON lines (<c>event</c> = <c>accepted</c> | <c>stage</c> | <c>log</c> | <c>result</c>) and exits 0 only for a
/// succeeded build. Cancellation: SIGINT/SIGTERM, or the request's <c>cancelFile</c> appearing.
/// </remarks>
internal static class IntegrationCommand
{
    public const string Contract = "hcg.executable-maker.integration/1";
    private static readonly JsonSerializerOptions Line = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Exit codes: 0 succeeded · 1 build failed/cancelled · 2 usage · 4 request refused (nothing was built).</summary>
    public static int Run(string[] args)
    {
        var verb = args.SkipWhile(a => a != "--integration").Skip(1).FirstOrDefault();
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        switch (verb)
        {
            case "describe":
                Console.WriteLine(Describe().ToJsonString(Pretty));
                Console.Out.Flush();
                return 0;
            case "build" when Option("--request") is { } request:
                return Build(request, Option("--result"));
            default:
                Console.Error.WriteLine($"Usage: HCGExecutableMaker --integration describe | build --request <request.json> [--result <result.json>]   ({Contract})");
                return 2;
        }
    }

    private static ServiceProvider Services()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var services = Composition.Build(paths);
        services.GetRequiredService<HCG.ExecutableMaker.Infrastructure.Toolchains.DeveloperDirectorySelection>().InitializeAsync().GetAwaiter().GetResult();
        return services;
    }

    public static JsonObject Describe()
    {
        using var services = Services();
        var toolchains = services.GetRequiredService<IToolchainService>().GetAsync().GetAwaiter().GetResult();
        var host = HostInfo.Detect();
        var targets = new JsonArray();
        foreach (var platform in new[] { TargetPlatform.MacOS, TargetPlatform.Windows, TargetPlatform.Linux })
        {
            var options = PackagingCatalog.Evaluate(new PackagingContext(platform, host, toolchains, IsGuiApplication: true));
            targets.Add(new JsonObject
            {
                ["platform"] = PlatformName(platform),
                ["architectures"] = new JsonArray("arm64", "x64"),
                ["formats"] = new JsonArray(options.Select(o => (JsonNode?)new JsonObject
                {
                    ["format"] = o.Format.ToString(),
                    ["displayName"] = o.DisplayName,
                    ["availability"] = o.Availability.ToString(),
                    ["reason"] = o.Reason,
                }).ToArray()),
            });
        }
        var dotnet = toolchains.Find(ToolchainIds.DotNetSdk);
        return new JsonObject
        {
            ["contract"] = Contract,
            ["product"] = "HCG Executable Maker",
            ["version"] = HostInfo.ProductVersion,
            ["commit"] = HostInfo.ProductCommit,
            ["buildIdentity"] = HostInfo.BuildIdentity,
            ["executable"] = HostInfo.ExecutablePath,
            ["host"] = new JsonObject
            {
                ["platform"] = PlatformName(host.Platform),
                ["architecture"] = host.Architecture == TargetArchitecture.Arm64 ? "arm64" : "x64",
                ["os"] = host.OsDescription,
                ["runtime"] = RuntimeInformation.FrameworkDescription,
            },
            ["operations"] = new JsonArray("describe", "build"),
            ["cancellation"] = new JsonArray("signal", "cancelFile"),
            ["buildProviders"] = new JsonArray(new JsonObject
            {
                ["id"] = "dotnet-desktop",
                ["description"] = ".NET desktop and console applications (Avalonia included): dotnet restore/build/publish, packaging, verification, SHA-256",
                ["available"] = dotnet?.IsAvailable == true,
                ["toolchain"] = dotnet?.Version,
                ["detail"] = dotnet?.IsAvailable == true ? null : dotnet?.Detail ?? ".NET SDK not found",
            }),
            ["notOfferedThroughThisContract"] = new JsonArray("Mobile Build Studio", "Swift packages", "Electron", "C/C++", "Porting Studio", "Impactor", "signing", "Ship to Store", "Ship to Repo"),
            ["releasePassport"] = new JsonObject { ["format"] = ReleasePassport.Format, ["formatVersion"] = ReleasePassport.FormatVersion, ["part"] = "build" },
            ["toolchains"] = new JsonArray(toolchains.Toolchains.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id, ["name"] = t.DisplayName, ["category"] = t.Category, ["status"] = t.Status.ToString(), ["version"] = t.Version, ["available"] = t.IsAvailable,
            }).ToArray()),
        };
    }

    private static int Build(string requestPath, string? resultPath)
    {
        JsonObject request;
        try { request = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject ?? throw new JsonException("not an object"); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Refuse(null, resultPath, $"request could not be read: {ex.Message}");
        }
        var requestId = Str(request, "requestId");
        if (Str(request, "contract") != Contract) return Refuse(requestId, resultPath, $"contract must be \"{Contract}\" (got {request["contract"]?.ToJsonString() ?? "none"})");
        if (request["trust"]?["acknowledged"]?.GetValue<bool>() != true)
            return Refuse(requestId, resultPath, "trust.acknowledged must be true: building runs the project's MSBuild code. " + HCG.ExecutableMaker.Security.TrustPolicy.TrustBoundaryExplanation);
        var source = Str(request["source"], "path");
        if (Str(request["source"], "kind") != "directory" || source is null || !Path.IsPathFullyQualified(source) || !Directory.Exists(source))
            return Refuse(requestId, resultPath, "source must be {\"kind\":\"directory\",\"path\":<absolute path of an existing folder>}");
        var output = Str(request, "outputDirectory");
        if (output is null || !Path.IsPathFullyQualified(output)) return Refuse(requestId, resultPath, "outputDirectory must be an absolute path");
        if (PathGuard.IsWithin(source, output)) return Refuse(requestId, resultPath, "outputDirectory must be outside the source folder");
        if (!TryPlatform(Str(request["target"], "platform"), out var platform) || !TryArch(Str(request["target"], "architecture"), out var arch))
            return Refuse(requestId, resultPath, "target must name platform macos|windows|linux and architecture arm64|x64");
        var identity = request["identity"] as JsonObject;
        var appName = Str(identity, "name");
        var appId = Str(identity, "id");
        var version = Str(identity, "version");
        if (appName is null || appId is null || version is null) return Refuse(requestId, resultPath, "identity needs name, id and version");
        var formats = new List<PackagingFormat>();
        foreach (var f in request["formats"] as JsonArray ?? [])
        {
            if (!Enum.TryParse<PackagingFormat>(f?.GetValue<string>(), out var format)) return Refuse(requestId, resultPath, $"unknown format {f?.ToJsonString()}");
            formats.Add(format);
        }
        if (formats.Count == 0) return Refuse(requestId, resultPath, "formats must name at least one packaging format");
        var cancelFile = Str(request, "cancelFile");

        using var services = Services();
        var projects = services.GetRequiredService<ProjectService>();
        var builds = services.GetRequiredService<BuildService>();
        var toolchains = services.GetRequiredService<IToolchainService>().GetAsync().GetAwaiter().GetResult();

        using var cts = new CancellationTokenSource();
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
        if (cancelFile is not null)
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (File.Exists(cancelFile)) { Emit(new JsonObject { ["event"] = "log", ["kind"] = "Info", ["text"] = "Cancel file found: cancelling the build" }); cts.Cancel(); }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            });

        ImportResult import;
        try { import = projects.ImportDirectoryAsync(source, null, cts.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is ImportRejectedException or IOException or UnauthorizedAccessException)
        {
            return Refuse(requestId, resultPath, "import refused: " + ex.Message);
        }
        var analysis = import.Project.Analysis;
        var projectPath = Str(request, "project") ?? analysis?.PrimaryProjectPath;
        var detected = analysis?.Projects.FirstOrDefault(p => string.Equals(p.RelativePath, projectPath, StringComparison.Ordinal));
        if (analysis is null || detected is null || analysis.InspectorId != "dotnet")
            return Refuse(requestId, resultPath, $"integration/1 builds .NET desktop projects only; inspection found: {analysis?.ApplicationType ?? "nothing"} ({analysis?.SupportSummary})");
        if (analysis.Support == SupportLevel.Unsupported)
            return Refuse(requestId, resultPath, "inspection reports the project as unsupported: " + analysis.SupportSummary);

        var isGui = detected.IsAvalonia || string.Equals(detected.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        var options = PackagingCatalog.Evaluate(new PackagingContext(platform, HostInfo.Detect(), toolchains, isGui));
        foreach (var format in formats)
        {
            var option = options.FirstOrDefault(o => o.Format == format);
            if (option is null || !option.IsAvailable)
                return Refuse(requestId, resultPath, $"{format} is not available for {PlatformName(platform)} on this computer: {option?.Reason ?? "not offered"}");
        }
        var icon = Str(identity, "icon");
        var settings = new BuildSettings
        {
            ProjectRelativePath = detected.RelativePath,
            Platform = platform,
            Architecture = arch,
            Configuration = Str(request, "configuration") == "Debug" ? BuildConfigurationKind.Debug : BuildConfigurationKind.Release,
            OutputDirectory = output,
            Timeout = TimeSpan.FromMinutes(Math.Clamp(request["timeoutMinutes"]?.GetValue<int>() ?? 30, 1, 240)),
            Identity = new AppIdentity
            {
                ApplicationName = appName, ApplicationId = appId, Version = version,
                Publisher = Str(identity, "publisher") ?? "", Description = Str(identity, "description") ?? "",
                IconSourcePath = icon is not null && Path.IsPathFullyQualified(icon) && File.Exists(icon) ? icon : null,
            },
            PackagingFormats = formats,
        };
        Emit(new JsonObject { ["event"] = "accepted", ["requestId"] = requestId, ["project"] = detected.RelativePath, ["runtimeIdentifier"] = settings.RuntimeIdentifier });

        BuildRecord build;
        try
        {
            build = builds.RunAsync(new BuildRequest
            {
                Project = import.Project, Settings = settings, TrustAcknowledged = true,
                AllowOverwriteOutput = request["allowOverwriteOutput"]?.GetValue<bool>() == true, AssemblyName = detected.AssemblyName,
            }, new Observer(), cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Refuse(requestId, resultPath, ex.Message);
        }

        JsonObject? passport = null;
        string? passportPath = null;
        var primary = build.Artifacts.FirstOrDefault(a => a.Kind == ArtifactKind.File) ?? build.Artifacts.FirstOrDefault();
        if (build.Status == BuildStatus.Succeeded && primary is not null && request["releasePassport"]?.GetValue<bool>() != false)
        {
            passport = ReleasePassport.DesktopBuildHalf(build, primary, HostInfo.BuildIdentity, Str(request["sourceRevision"], "repository"), Str(request["sourceRevision"], "commit"));
            var problems = ReleasePassport.ValidateBuildHalf(passport);
            if (problems.Count > 0) { passport = null; Emit(new JsonObject { ["event"] = "log", ["kind"] = "Warning", ["text"] = "Release Passport not written: " + string.Join("; ", problems) }); }
            else
            {
                passportPath = Path.Combine(BuildService.OutputRootFor(settings), ReleasePassport.FileName);
                File.WriteAllText(passportPath, passport.ToJsonString(Pretty));
            }
        }

        var result = new JsonObject
        {
            ["event"] = "result",
            ["contract"] = Contract,
            ["requestId"] = requestId,
            ["status"] = build.Status.ToString(),
            ["failureReason"] = build.FailureReason,
            ["buildId"] = build.Id.ToString(),
            ["builtBy"] = HostInfo.BuildIdentity,
            ["startedAt"] = build.StartedAt.ToString("O"),
            ["endedAt"] = build.EndedAt?.ToString("O"),
            ["runtimeIdentifier"] = settings.RuntimeIdentifier,
            ["project"] = detected.RelativePath,
            ["logPath"] = build.LogPath,
            ["warningCount"] = build.WarningCount,
            ["errorCount"] = build.ErrorCount,
            ["toolchains"] = new JsonObject(build.ToolchainVersions.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["diagnostics"] = new JsonArray(build.Messages.Take(500).Select(m => (JsonNode?)new JsonObject
            {
                ["severity"] = m.Severity.ToString(), ["text"] = m.Text, ["code"] = m.Code, ["file"] = m.File, ["line"] = m.Line,
            }).ToArray()),
            ["artifacts"] = new JsonArray(build.Artifacts.Select(a => (JsonNode?)new JsonObject
            {
                ["format"] = a.Format.ToString(), ["kind"] = a.Kind.ToString(), ["path"] = a.Path, ["sizeBytes"] = a.SizeBytes,
                ["sha256"] = a.Sha256, ["verification"] = a.Verification.ToString(),
                ["checks"] = new JsonArray(a.Checks.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail }).ToArray()),
            }).ToArray()),
            ["releasePassport"] = passportPath is null ? null : new JsonObject { ["path"] = passportPath, ["sha256"] = Checksums.Sha256File(passportPath) },
        };
        Finish(result, resultPath);
        return build.Status == BuildStatus.Succeeded ? 0 : 1;
    }

    private sealed class Observer : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => Emit(new JsonObject { ["event"] = "stage", ["stage"] = stage.ToString(), ["description"] = description });
        public void OnLog(BuildLogLine line) => Emit(new JsonObject { ["event"] = "log", ["kind"] = line.Kind.ToString(), ["text"] = line.Text });
    }

    private static readonly Lock EmitLock = new();
    private static void Emit(JsonObject e)
    {
        lock (EmitLock) Console.Out.WriteLine(e.ToJsonString(Line));
    }

    private static int Refuse(string? requestId, string? resultPath, string reason)
    {
        Finish(new JsonObject { ["event"] = "result", ["contract"] = Contract, ["requestId"] = requestId, ["status"] = "Refused", ["failureReason"] = reason, ["artifacts"] = new JsonArray() }, resultPath);
        return 4;
    }

    private static void Finish(JsonObject result, string? resultPath)
    {
        Emit(result);
        if (resultPath is not null) File.WriteAllText(resultPath, result.ToJsonString(Pretty));
        Console.Out.Flush();
    }

    private static string? Str(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string PlatformName(TargetPlatform p) => p switch { TargetPlatform.MacOS => "macos", TargetPlatform.Windows => "windows", _ => p.ToString().ToLowerInvariant() };

    private static bool TryPlatform(string? s, out TargetPlatform p)
    {
        p = s switch { "macos" => TargetPlatform.MacOS, "windows" => TargetPlatform.Windows, "linux" => TargetPlatform.Linux, _ => (TargetPlatform)(-1) };
        return (int)p >= 0;
    }

    private static bool TryArch(string? s, out TargetArchitecture a)
    {
        a = s == "arm64" ? TargetArchitecture.Arm64 : TargetArchitecture.X64;
        return s is "arm64" or "x64";
    }
}
