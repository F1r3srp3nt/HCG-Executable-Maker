namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Android Gradle Plugin → minimum Gradle / JDK, from Google's published compatibility table.
/// Versions newer than the table are reported as "not verifiable", never assumed compatible.
/// </summary>
public static class AgpCompatibility
{
    private static readonly (Version Agp, Version MinGradle, int MinJdk)[] Table =
    [
        (new(7, 0), new(7, 0), 11), (new(7, 1), new(7, 2), 11), (new(7, 2), new(7, 3, 3), 11), (new(7, 3), new(7, 4), 11),
        (new(7, 4), new(7, 5), 11), (new(8, 0), new(8, 0), 17), (new(8, 1), new(8, 0), 17), (new(8, 2), new(8, 2), 17),
        (new(8, 3), new(8, 4), 17), (new(8, 4), new(8, 6), 17), (new(8, 5), new(8, 7), 17), (new(8, 6), new(8, 7), 17),
        (new(8, 7), new(8, 9), 17), (new(8, 8), new(8, 10, 2), 17), (new(8, 9), new(8, 11, 1), 17), (new(8, 10), new(8, 11, 1), 17),
        (new(8, 11), new(8, 13), 17), (new(8, 12), new(8, 13), 17), (new(8, 13), new(8, 13), 17),
    ];

    public sealed record Result(bool? GradleOk, bool? JdkOk, string Message);

    public static Result Check(string? agpVersion, string? gradleVersion, int? jdkMajor)
    {
        if (agpVersion is null || !TryParse(agpVersion, out var agp))
            return new Result(null, null, "Android Gradle Plugin version could not be determined statically.");
        var row = Table.Where(r => r.Agp.Major == agp.Major && r.Agp.Minor == agp.Minor).Select(r => ((Version, Version, int)?)r).FirstOrDefault();
        if (row is null)
            return new Result(null, null, $"AGP {agpVersion} is newer than HCG's compatibility table; Gradle/JDK compatibility will be confirmed by the build.");
        var (_, minGradle, minJdk) = row.Value;
        bool? gradleOk = gradleVersion is not null && TryParse(gradleVersion, out var g) ? g >= minGradle : null;
        bool? jdkOk = jdkMajor is { } j ? j >= minJdk : null;
        var parts = new List<string> { $"AGP {agpVersion} requires Gradle {minGradle}+ and JDK {minJdk}+." };
        if (gradleOk == false) parts.Add($"The project's Gradle wrapper ({gradleVersion}) is too old.");
        if (jdkOk == false) parts.Add($"The detected JDK ({jdkMajor}) is too old.");
        return new Result(gradleOk, jdkOk, string.Join(' ', parts));
    }

    public static bool TryParse(string s, out Version v)
    {
        var core = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
        if (!core.Contains('.')) core += ".0";
        return Version.TryParse(core, out v!);
    }
}
