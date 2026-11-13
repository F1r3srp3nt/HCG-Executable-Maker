namespace HCG.ExecutableMaker.Account;

/// <summary>
/// Imports the public HCG Connect configuration HCG issues for this app (hcg-connect.json): validated with the same rules
/// as at startup (no secrets, known keys only, this contract version) and ready for sign-in (an issued UUID client ID,
/// loopback redirect URIs, openid, HTTPS identity endpoints), then written to the app data folder. On any problem
/// nothing is changed.
/// </summary>
public static class HcgConnectImport
{
    public sealed record Result(HcgConnectConfiguration? Configuration, string? Error);

    public static Result Import(string file, string appDataRoot)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return new(null, $"{file} does not exist. Nothing was changed.");
        if (info.Length > HcgConnectConfiguration.MaxFileBytes)
            return new(null, $"{info.Name} is {info.Length / 1024:N0} KB; an HCG Connect configuration is about 1 KB. Nothing was changed.");
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, $"{info.Name} could not be read ({ex.Message}). Nothing was changed."); }
        var (config, problem) = HcgConnectConfiguration.Parse(text);
        if (problem is not null) return new(null, problem.Replace("The file was ignored.", "Nothing was changed.", StringComparison.Ordinal));
        var missing = config.MissingForSignIn();
        if (missing.Count > 0) return new(null, $"{info.Name} cannot be used for sign-in: {string.Join("; ", missing)}. Nothing was changed.");

        Directory.CreateDirectory(appDataRoot);
        var target = Path.Combine(appDataRoot, HcgConnectConfiguration.FileName);
        var temp = target + ".importing";
        File.WriteAllText(temp, text);
        if (File.Exists(target)) File.Copy(target, Path.Combine(appDataRoot, "hcg-connect.previous.json"), overwrite: true);
        File.Move(temp, target, overwrite: true);
        return new(config, null);
    }
}
