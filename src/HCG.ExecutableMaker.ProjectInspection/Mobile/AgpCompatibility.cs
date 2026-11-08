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
        // developer.android.com/build/releases/gradle-plugin (fetched 2026-11-08): AGP 9.5 → Gradle 9.6.0, JDK 17.
        (new(9, 5), new(9, 6, 0), 17),
    ];

    /// <summary>Minimum JDK for an AGP major version even when the exact minor is not in the table (AGP 8+ → 17, AGP 7 → 11).</summary>
    public static int? MinJdkForAgp(string? agpVersion) =>
        agpVersion is not null && TryParse(agpVersion, out var v) ? v.Major >= 8 ? 17 : v.Major == 7 ? 11 : null : null;

    /// <summary>
    /// Java versions Gradle can RUN on: Java N needs at least this Gradle version. From Gradle's compatibility matrix
    /// (docs.gradle.org/current/userguide/compatibility.html; that host was not reachable from HCG's build environment
    /// to re-check, so a JDK newer than the last row is reported as "not verifiable", never as compatible).
    /// </summary>
    private static readonly (int Java, Version MinGradle)[] GradleRunsOnJava =
    [
        (8, new(2, 0)), (9, new(4, 3)), (10, new(4, 7)), (11, new(5, 0)), (12, new(5, 4)), (13, new(6, 0)), (14, new(6, 3)),
        (15, new(6, 7)), (16, new(7, 0)), (17, new(7, 3)), (18, new(7, 5)), (19, new(7, 6)), (20, new(8, 3)), (21, new(8, 5)),
        (22, new(8, 8)), (23, new(8, 10)), (24, new(8, 14)), (25, new(9, 1)),
    ];

    public sealed record JavaFit(bool? Ok, string Message);

    /// <summary>Can this Gradle version run on this JDK, and is the JDK new enough for the AGP version?</summary>
    public static JavaFit CheckJava(int jdkMajor, string? gradleVersion, string? agpVersion)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var minJdk = Table.Where(r => agpVersion is not null && TryParse(agpVersion, out var a) && r.Agp.Major == a.Major && r.Agp.Minor == a.Minor)
                         .Select(r => (int?)r.MinJdk).FirstOrDefault() ?? MinJdkForAgp(agpVersion);
        if (minJdk is { } min && jdkMajor < min) problems.Add($"AGP {agpVersion} needs JDK {min} or newer; the selected JDK is {jdkMajor}.");
        if (gradleVersion is not null && TryParse(gradleVersion, out var g))
        {
            if (g.Major >= 9 && jdkMajor < 17) problems.Add($"Gradle {gradleVersion} needs JDK 17 or newer to run.");
            var row = GradleRunsOnJava.FirstOrDefault(r => r.Java == jdkMajor);
            if (row.Java == jdkMajor && row.MinGradle is not null && g < row.MinGradle)
                problems.Add($"Gradle {gradleVersion} cannot run on JDK {jdkMajor} (JDK {jdkMajor} needs Gradle {row.MinGradle}+). Use an older JDK (e.g. 17) or upgrade the project's Gradle wrapper.");
            else if (jdkMajor > GradleRunsOnJava[^1].Java)
                notes.Add($"JDK {jdkMajor} is newer than HCG's Gradle compatibility table; whether Gradle {gradleVersion} runs on it is confirmed only by the build. JDK 17 or 21 is the safe choice.");
        }
        else notes.Add("The project's Gradle version is unknown, so Gradle/JDK compatibility is confirmed only by the build.");
        if (problems.Count > 0) return new JavaFit(false, string.Join(" ", problems));
        return notes.Count > 0 ? new JavaFit(null, string.Join(" ", notes)) : new JavaFit(true, $"JDK {jdkMajor} fits Gradle {gradleVersion}{(agpVersion is null ? "" : $" and AGP {agpVersion}")}.");
    }

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
