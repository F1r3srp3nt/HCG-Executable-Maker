using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Porting.Android;

/// <summary>C/C++ source with comments blanked and preprocessor conditionals evaluated for Android (bionic).</summary>
internal sealed class CSource
{
    public string Path { get; }
    public IReadOnlyList<string> Lines { get; }
    /// <summary>Comments blanked; string literals kept (absolute-path checks need them).</summary>
    public IReadOnlyList<string> Code { get; }
    /// <summary>Comments and string/char literal contents blanked (for API matching).</summary>
    public IReadOnlyList<string> Bare { get; }
    public IReadOnlyList<bool> ActiveOnAndroid { get; }

    public CSource(string path, string text)
    {
        Path = path;
        text = text.Replace("\r\n", "\n");
        Lines = text.Split('\n');
        Code = Strip(text, keepStrings: true).Split('\n');
        Bare = Strip(text, keepStrings: false).Split('\n');
        ActiveOnAndroid = Conditions(Code);
    }

    internal static string Strip(string s, bool keepStrings)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
                if (i < s.Length) sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                sb.Append("  "); i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { sb.Append(s[i] == '\n' ? '\n' : ' '); i++; }
                if (i < s.Length) { sb.Append("  "); i++; }
                continue;
            }
            if (c is '"' or '\'')
            {
                sb.Append(c);
                i++;
                while (i < s.Length && s[i] != c && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(keepStrings ? s[i] : ' ').Append(keepStrings ? s[i + 1] : ' '); i += 2; continue; }
                    sb.Append(keepStrings ? s[i] : ' ');
                    i++;
                }
                if (i < s.Length) sb.Append(s[i]);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> Defined = new(StringComparer.Ordinal)
        { "__linux__", "__linux", "linux", "__unix__", "__unix", "unix", "__ANDROID__", "__BIONIC__", "__GNUC__", "__clang__", "__STDC__", "__ELF__", "_POSIX_C_SOURCE", "__aarch64__", "__x86_64__" };
    private static readonly HashSet<string> Undefined = new(StringComparer.Ordinal)
        { "_WIN32", "_WIN64", "WIN32", "_MSC_VER", "__APPLE__", "__MACH__", "__FreeBSD__", "__OpenBSD__", "__NetBSD__", "__CYGWIN__", "__MINGW32__", "__GLIBC__", "__GNU_LIBRARY__", "__EMSCRIPTEN__", "__sun" };

    private static bool[] Conditions(IReadOnlyList<string> code)
    {
        var active = new bool[code.Count];
        var stack = new Stack<(bool Parent, bool Taken)>();
        var cur = true;
        for (var n = 0; n < code.Count; n++)
        {
            var t = code[n].Trim();
            if (t.StartsWith('#')) t = "#" + t[1..].TrimStart();
            bool? cond = t.StartsWith("#ifdef ", StringComparison.Ordinal) ? Macro(t[7..].Trim())
                : t.StartsWith("#ifndef ", StringComparison.Ordinal) ? !Macro(t[8..].Trim())
                : t.StartsWith("#if ", StringComparison.Ordinal) ? Eval(t[4..]) : null;
            if (cond is { } v)
            {
                active[n] = cur;
                stack.Push((cur, v));
                cur = cur && v;
                continue;
            }
            if ((t.StartsWith("#elif ", StringComparison.Ordinal) || t.StartsWith("#else", StringComparison.Ordinal)) && stack.Count > 0)
            {
                var (parent, taken) = stack.Pop();
                var now = !taken && (t.StartsWith("#else", StringComparison.Ordinal) || Eval(t[6..]));
                stack.Push((parent, taken || now));
                active[n] = parent;
                cur = parent && now;
                continue;
            }
            if (t.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
            {
                cur = stack.Pop().Parent;
                active[n] = cur;
                continue;
            }
            active[n] = cur;
        }
        return active;
    }

    private static bool Macro(string name) => !Undefined.Contains(name.Split(' ', '\t')[0]);

    /// <summary>Evaluates defined()/!/&&/|| over platform macros; anything else counts as true (analysed, not skipped).</summary>
    private static bool Eval(string expr)
    {
        expr = Regex.Replace(expr, @"defined\s*\(\s*(\w+)\s*\)|defined\s+(\w+)", m =>
            Undefined.Contains(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value) ? "0" : "1");
        expr = Regex.Replace(expr, @"\b[A-Za-z_]\w*\b", m => Undefined.Contains(m.Value) ? "0" : Defined.Contains(m.Value) ? "1" : "1");
        try
        {
            var i = 0;
            return Or(expr.Replace(" ", "").Replace("\t", ""), ref i);
        }
        catch (FormatException) { return true; }
    }

    private static bool Or(string s, ref int i) { var v = And(s, ref i); while (i + 1 < s.Length && s[i] == '|' && s[i + 1] == '|') { i += 2; v = And(s, ref i) | v; } return v; }
    private static bool And(string s, ref int i) { var v = Unary(s, ref i); while (i + 1 < s.Length && s[i] == '&' && s[i + 1] == '&') { i += 2; v = Unary(s, ref i) & v; } return v; }
    private static bool Unary(string s, ref int i)
    {
        if (i < s.Length && s[i] == '!') { i++; return !Unary(s, ref i); }
        if (i < s.Length && s[i] == '(') { i++; var v = Or(s, ref i); if (i < s.Length && s[i] == ')') i++; return v; }
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '>' or '<' or '=' or '+' or '-' or '*')) i++;
        if (start == i) throw new FormatException();
        var token = s[start..i];
        return token != "0";
    }
}

internal sealed class CFilePlan(CSource source)
{
    public CSource Source { get; } = source;
    public List<PortingFinding> Findings { get; } = [];
    public bool DefinesMain { get; set; }
    public bool HasBlockers => Findings.Any(f => f.Disposition == FindingDisposition.Blocker);
    public bool UsesNetwork { get; set; }
}

/// <summary>Linux → Android rules for C/C++ (bionic libc, app sandbox, no terminal).</summary>
internal static partial class LinuxToAndroidRules
{
    private sealed record Rule(Regex Pattern, FindingCategory Category, FindingDisposition Disposition, string Title, string Detail, string? Guidance);

    private static Rule R(string pattern, FindingCategory c, FindingDisposition d, string title, string detail, string? guidance = null) =>
        new(new Regex(pattern, RegexOptions.Compiled), c, d, title, detail, guidance);

    private const FindingDisposition B = FindingDisposition.Blocker;
    private const FindingDisposition V = FindingDisposition.NeedsReview;
    private const FindingDisposition A = FindingDisposition.AutoAdapted;

    /// <summary>Headers matched against #include lines.</summary>
    private static readonly Rule[] Headers =
    [
        R(@"^(X11|Xm|GL/glx)\b", FindingCategory.UiAdaptation, B, "X11 / GLX", "Android has no X server.", "Rewrite the UI for Android (Java/Kotlin views or an Android-capable toolkit)."),
        R(@"^(gtk|gdk|gtkmm|glibmm|giomm|cairo|pango)\b", FindingCategory.UiAdaptation, B, "GTK / Cairo / Pango", "GTK applications have no Android port.", "Rewrite the UI; keep the non-UI code in a library that this port can reuse."),
        R(@"^(glib\.h|gio/|glib/|glib-object\.h)", FindingCategory.Dependency, B, "GLib", "GLib is not part of the Android NDK.", "Build GLib for Android yourself or replace its use."),
        R(@"^(Q[A-Z]\w*$|Qt\w*/)", FindingCategory.UiAdaptation, B, "Qt", "Qt apps need Qt for Android and its own Android build (qt-cmake / androiddeployqt).", "Use Qt for Android directly; HCG does not generate Qt Android projects."),
        R(@"^(SDL2?/|SDL\.h)", FindingCategory.UiAdaptation, B, "SDL", "SDL supports Android only with its SDLActivity Java glue.", "Use SDL's android-project template; HCG v1 generates console hosts only."),
        R(@"^(ncurses|curses|ncursesw|panel|menu|form)\.h$|^ncursesw?/", FindingCategory.UiAdaptation, B, "curses terminal UI", "There is no terminal on Android; full-screen text UIs cannot be shown in the console host.", "Port the interface to Android views."),
        R(@"^(GL/gl\.h|GL/glu\.h|GL/glew\.h|GLFW/)", FindingCategory.UiAdaptation, B, "Desktop OpenGL / GLFW", "Android uses OpenGL ES / Vulkan with an Android surface.", "Port rendering to GLES/Vulkan with an Android activity."),
        R(@"^(wayland-|alsa/|pulse/|libudev\.h|dbus/|systemd/|libnotify/)", FindingCategory.Dependency, B, "Linux desktop service API", "Wayland, ALSA, PulseAudio, udev, D-Bus and systemd are not available to Android apps.", "Use Android APIs (AAudio, Java services) instead."),
        R(@"^sys/(shm|sem|msg)\.h$", FindingCategory.PlatformApi, B, "System V IPC", "shmget/semget/msgget are blocked for apps by Android's security policy.", "Use threads, pipes, or ASharedMemory."),
        R(@"^(execinfo\.h)$", FindingCategory.PlatformApi, B, "execinfo.h backtrace", "backtrace() exists in bionic only from API 33 (this port targets API 24+).", "Remove it, or raise minSdk to 33."),
        R(@"^(gnu/libc-version\.h|crypt\.h|wordexp\.h|libintl\.h|obstack\.h|argp\.h|error\.h|fstab\.h|mcheck\.h)$", FindingCategory.PlatformApi, B, "glibc-only header", "This header is part of glibc, not Android's bionic libc.", "Replace the glibc-specific API."),
        R(@"^(curl/|openssl/|ssl\.h|sqlite3\.h|png\.h|jpeglib\.h|turbojpeg\.h|archive\.h|yaml\.h|uv\.h|event2?/|boost/(?!.*hpp$))", FindingCategory.Dependency, B, "Third-party native library", "The library must be built for Android (each ABI) and linked; HCG v1 does not build third-party native dependencies.", "Add an Android build of the library (e.g. a prefab AAR or prebuilt .so) to the generated CMake project."),
        R(@"^(iconv\.h|glob\.h|spawn\.h|sys/random\.h)$", FindingCategory.PlatformApi, V, "Header needs a newer Android API level", "iconv, glob, posix_spawn and getrandom exist in bionic only from API 28 (this port's minSdk is 24).", "Raise minSdk to 28 in app/build.gradle.kts, or avoid the API."),
    ];

    private static readonly Rule[] Calls =
    [
        R(@"\bpthread_(cancel|setcancelstate|setcanceltype|testcancel)\s*\(", FindingCategory.PlatformApi, B, "pthread cancellation", "Bionic does not implement pthread cancellation.", "Use a flag or condition variable to stop threads."),
        R(@"\b(shmget|semget|msgget|shmat)\s*\(", FindingCategory.PlatformApi, B, "System V IPC call", "Blocked for apps by Android's security policy.", "Use threads/pipes or ASharedMemory."),
        R(@"\b(backtrace|backtrace_symbols)\s*\(", FindingCategory.PlatformApi, B, "backtrace()", "Available in bionic only from API 33.", "Remove it or raise minSdk to 33."),
        R(@"\b(system|popen)\s*\(", FindingCategory.PlatformApi, V, "Runs shell commands", "Android has /system/bin/sh, but most desktop utilities do not exist and the app sandbox restricts what runs.", "Replace the command with code, or check what exists on the device."),
        R(@"\b(fork|vfork|execv|execvp|execve|execl|execlp|posix_spawn)\s*\(", FindingCategory.PlatformApi, V, "Starts processes", "Allowed, but desktop programs it may start are not on Android devices.", "Avoid depending on external programs."),
        R(@"\b(getpwuid|getpwnam|getlogin|getgrgid|getgrnam)\s*\(", FindingCategory.PlatformApi, V, "User database lookups", "Android apps run as per-app users (e.g. u0_a123); there are no login accounts or home directories in /etc/passwd.", "Use the app's files directory (HOME is set to it)."),
        R(@"\b(isatty|tcgetattr|tcsetattr|ioctl\s*\([^,]*,\s*TIOCGWINSZ)", FindingCategory.UiAdaptation, V, "Terminal control", "The console host is not a terminal: isatty() is false, raw mode and window size are unavailable.", "Make terminal features optional."),
        R(@"\\033\[|\\x1[bB]\[|\\e\[", FindingCategory.UiAdaptation, A, "ANSI escape sequences", "The console host strips ANSI colour/cursor codes before display."),
        R(@"\bsignal\s*\(\s*SIG(INT|TERM|HUP|TSTP|WINCH)|sigaction\s*\(\s*SIG(INT|TERM|HUP|TSTP|WINCH)", FindingCategory.Lifecycle, V, "Terminal signals", "Android never delivers Ctrl-C/terminal signals to the program.", "Stop work from the program's own logic."),
        R(@"\b(_exit|_Exit|quick_exit|abort)\s*\(", FindingCategory.Lifecycle, V, "Terminates the process", "_exit/abort end the whole Android app (only exit() is intercepted by the host).", "Return from main instead."),
        R(@"\b(dlopen)\s*\(\s*""(?!\./)[^""]*lib", FindingCategory.PlatformApi, V, "dlopen of a system library", "Android exposes only NDK system libraries to apps; desktop library names differ.", "Load only libraries packaged with the app."),
    ];

    [GeneratedRegex(@"^\s*#\s*include\s*<([^>]+)>")]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"""(/(etc|usr|var|home|tmp|opt|proc|sys|run|srv|root)(/[^""]*)?)""")]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex(@"\b(fgets|fgetc|getc|getline|getdelim|fread|read)\s*\([^)]*\b(stdin|STDIN_FILENO|0)\s*[),]|\b(scanf|getchar|gets)\s*\(|\bstd::cin\b|\bstd::getline\s*\(\s*std::cin")]
    private static partial Regex StdinRegex();

    [GeneratedRegex(@"\b(socket|connect|getaddrinfo|gethostbyname)\s*\(")]
    private static partial Regex NetworkRegex();

    [GeneratedRegex(@"\b(exit)\s*\(")]
    private static partial Regex ExitRegex();

    [GeneratedRegex(@"^\s*(int|auto)\s+main\s*\(")]
    private static partial Regex MainRegex();

    public static CFilePlan Analyze(CSource src)
    {
        var plan = new CFilePlan(src);
        PortingFinding F(Rule r, int line, string? evidence = null) => new()
        {
            Category = r.Category, Disposition = r.Disposition, Title = r.Title, Detail = r.Detail, Guidance = r.Guidance,
            File = src.Path, Line = line + 1, Evidence = evidence ?? src.Lines[line].Trim(),
        };
        var stdinReported = false;
        var exitReported = false;
        for (var n = 0; n < src.Lines.Count; n++)
        {
            if (!src.ActiveOnAndroid[n]) continue;
            var code = src.Code[n];
            var bare = src.Bare[n];
            if (IncludeRegex().Match(code) is { Success: true } inc)
            {
                var header = inc.Groups[1].Value.Trim();
                if (Headers.FirstOrDefault(h => h.Pattern.IsMatch(header)) is { } rule) plan.Findings.Add(F(rule, n));
                continue;
            }
            if (bare.TrimStart().StartsWith('#')) continue;
            if (MainRegex().IsMatch(bare)) plan.DefinesMain = true;
            foreach (var rule in Calls)
                if (rule.Pattern.IsMatch(rule.Title.StartsWith("ANSI", StringComparison.Ordinal) ? code : bare) && !plan.Findings.Any(f => f.Line == n + 1 && f.Title == rule.Title))
                    plan.Findings.Add(F(rule, n));
            if (AbsolutePathRegex().Match(code) is { Success: true } path)
                plan.Findings.Add(new PortingFinding
                {
                    Category = FindingCategory.FileSystem, Disposition = V, Title = $"Absolute path \"{path.Groups[1].Value}\"",
                    Detail = "An Android app sees only its own sandbox; system paths differ and /tmp does not exist. The host sets HOME and TMPDIR to the app's files and cache folders and starts in the files folder.",
                    Guidance = "Use relative paths, getenv(\"HOME\") or getenv(\"TMPDIR\").", File = src.Path, Line = n + 1, Evidence = src.Lines[n].Trim(),
                });
            if (!stdinReported && StdinRegex().IsMatch(bare))
            {
                stdinReported = true;
                plan.Findings.Add(new PortingFinding
                {
                    Category = FindingCategory.UiAdaptation, Disposition = A, Title = "Reads standard input",
                    Detail = "The console host connects stdin to an input field (Send writes a line; Close input sends end-of-file).",
                    File = src.Path, Line = n + 1, Evidence = src.Lines[n].Trim(),
                });
            }
            if (!exitReported && ExitRegex().IsMatch(bare) && !bare.Contains("_exit", StringComparison.Ordinal))
            {
                exitReported = true;
                plan.Findings.Add(new PortingFinding
                {
                    Category = FindingCategory.Lifecycle, Disposition = A, Title = "Calls exit()",
                    Detail = "exit() is intercepted (linker --wrap) and returns control to the console host with the exit code instead of closing the app. atexit handlers and C++ destructors of objects on the stack are not run.",
                    File = src.Path, Line = n + 1, Evidence = src.Lines[n].Trim(),
                });
            }
            if (NetworkRegex().IsMatch(bare)) plan.UsesNetwork = true;
        }
        return plan;
    }
}
