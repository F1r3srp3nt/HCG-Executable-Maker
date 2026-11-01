using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.DotNet;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

public static partial class FlutterAnalyzer
{
    /// <summary>pubspec.yaml files that declare a Flutter app (sdk: flutter dependency).</summary>
    public static IReadOnlyList<string> FindPubspecs(SourceIndex index) =>
        index.Named("pubspec.yaml").OrderBy(SourceIndex.Depth)
            .Where(p => FlutterDependencyRegex().IsMatch(AndroidGradleAnalyzer.Read(index, p)))
            .ToList();

    public static FlutterProjectInfo Analyze(SourceIndex index, string pubspec)
    {
        var text = AndroidGradleAnalyzer.Read(index, pubspec);
        var root = AndroidGradleAnalyzer.Dir(pubspec);
        string? Top(string key) => Regex.Match(text, @"^" + key + @":\s*['""]?([^'""\n#]+?)['""]?\s*(?:#.*)?$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        return new FlutterProjectInfo
        {
            Root = root,
            Name = Top("name") ?? new DirectoryInfo(index.FullPath(root.Length == 0 ? "." : root)).Name,
            Version = Top("version"),
            SdkConstraint = Regex.Match(text, @"^\s+sdk:\s*['""]?([^'""\n]*\d[^'""\n]*)['""]?", RegexOptions.Multiline) is { Success: true } s ? s.Groups[1].Value.Trim() : null,
            HasAndroid = index.Files.Any(f => f.StartsWith(AndroidGradleAnalyzer.Join(root, "android/app/build.gradle"), StringComparison.Ordinal)),
            HasIos = index.Files.Any(f => f.StartsWith(AndroidGradleAnalyzer.Join(root, "ios/Runner.xcodeproj/"), StringComparison.Ordinal)),
        };
    }

    [GeneratedRegex(@"^\s+flutter:\s*\n\s+sdk:\s*flutter", RegexOptions.Multiline)]
    private static partial Regex FlutterDependencyRegex();
}

public static class MauiAnalyzer
{
    /// <summary>.csproj files with UseMaui=true and at least one mobile target framework.</summary>
    public static IReadOnlyList<MauiProjectInfo> Find(SourceIndex index)
    {
        var list = new List<MauiProjectInfo>();
        foreach (var rel in index.WithExtension(".csproj"))
        {
            MsBuildProjectFile file;
            try { file = MsBuildProjectFile.Parse(index.FullPath(rel)); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { continue; }
            if (!file.IsTrue("UseMaui")) continue;
            var tfms = (file.Get("TargetFrameworks") ?? file.Get("TargetFramework") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (!tfms.Any(t => t.Contains("-android", StringComparison.OrdinalIgnoreCase) || t.Contains("-ios", StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new MauiProjectInfo
            {
                ProjectPath = rel,
                ApplicationId = file.Get("ApplicationId"),
                ApplicationTitle = file.Get("ApplicationTitle"),
                DisplayVersion = file.Get("ApplicationDisplayVersion"),
                TargetFrameworks = tfms,
            });
        }
        return list;
    }
}
