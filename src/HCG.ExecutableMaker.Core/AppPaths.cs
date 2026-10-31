namespace HCG.ExecutableMaker.Core;

/// <summary>Locations of HCG Executable Maker's own data. Imported projects never live outside <see cref="WorkspacesDirectory"/>.</summary>
public interface IAppPaths
{
    string Root { get; }
    string DatabasePath { get; }
    string WorkspacesDirectory { get; }
    string BuildsDirectory { get; }
    string LogsDirectory { get; }
    string DefaultOutputDirectory { get; }
    void EnsureCreated();
}

public sealed class AppPaths : IAppPaths
{
    public const string RootOverrideVariable = "HCG_EXECUTABLE_MAKER_HOME";

    public AppPaths(string root, string? defaultOutputDirectory = null)
    {
        Root = Path.GetFullPath(root);
        DefaultOutputDirectory = defaultOutputDirectory ?? Path.Combine(Root, "output");
    }

    public static AppPaths CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return new AppPaths(overrideRoot);
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var output = string.IsNullOrEmpty(docs) ? null : Path.Combine(docs, "HCG Executable Maker", "Output");
        return new AppPaths(Path.Combine(baseDir, "HCG", "ExecutableMaker"), output);
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "hcg-executable-maker.db");
    public string WorkspacesDirectory => Path.Combine(Root, "workspaces");
    public string BuildsDirectory => Path.Combine(Root, "builds");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string DefaultOutputDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(WorkspacesDirectory);
        Directory.CreateDirectory(BuildsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
