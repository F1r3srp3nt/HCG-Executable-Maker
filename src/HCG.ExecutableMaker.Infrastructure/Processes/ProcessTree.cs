using System.Diagnostics;

namespace HCG.ExecutableMaker.Infrastructure.Processes;

/// <summary>
/// Kills a process and everything it started, without ever stopping a process.
/// </summary>
/// <remarks>
/// .NET's <c>Process.Kill(entireProcessTree: true)</c> on Unix sends SIGSTOP to each process, lists its children, then sends
/// SIGKILL. When that fails part-way (a process that cannot be enumerated, a child that re-parents), processes stay STOPPED.
/// Stopped processes that lose their parent make their process group orphaned, and the kernel then sends SIGHUP to every
/// process in that group (POSIX job control). CI run 101 (port-ios, repeat 2): a toolchain probe timed out after 20 s, its tree
/// kill left four bash processes stopped under launchd, and the UI driver — in the same process group — died of SIGHUP
/// (exit 129) two seconds later. Here the whole tree is collected first (parent links still intact), then every member is
/// sent SIGKILL; a second pass catches anything forked meanwhile. Nothing is ever stopped.
/// </remarks>
public static class ProcessTree
{
    public static void Kill(Process process)
    {
        int root;
        try { root = process.Id; } catch (InvalidOperationException) { return; }
        if (OperatingSystem.IsWindows())
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return;
        }
        var killed = new HashSet<int>();
        for (var pass = 0; pass < 3; pass++)
        {
            var tree = Descendants(root, killed);
            if (pass > 0 && tree.All(killed.Contains)) break;
            foreach (var pid in tree)
            {
                if (!killed.Add(pid)) continue;
                try { using var p = Process.GetProcessById(pid); p.Kill(); }   // SIGKILL; a stopped process dies too
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /// <summary>The root and every process whose parent chain leads to it (or to an already-killed member), from ps.</summary>
    internal static IReadOnlyList<int> Descendants(int root, IReadOnlySet<int> alsoParents)
    {
        var parents = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in Table())
        {
            if (!parents.TryGetValue(ppid, out var list)) parents[ppid] = list = [];
            list.Add(pid);
        }
        var result = new List<int> { root };
        var queue = new Queue<int>([root, .. alsoParents]);
        var seen = new HashSet<int>(queue);
        while (queue.Count > 0)
            foreach (var child in parents.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(child)) { result.Add(child); queue.Enqueue(child); }
        return result;
    }

    /// <summary>(pid, ppid) of every process, from /bin/ps (macOS and Linux).</summary>
    internal static IEnumerable<(int Pid, int Ppid)> Table()
    {
        var ps = File.Exists("/bin/ps") ? "/bin/ps" : "/usr/bin/ps";
        var psi = new ProcessStartInfo(ps) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-A", "-o", "pid=", "-o", "ppid=" }) psi.ArgumentList.Add(a);
        string output;
        try
        {
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { yield break; }
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid)) yield return (pid, ppid);
        }
    }
}
