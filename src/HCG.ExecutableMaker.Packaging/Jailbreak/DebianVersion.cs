namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>dpkg's version ordering (epoch, upstream version, revision; '~' sorts before everything, even the end).</summary>
public static class DebianVersion
{
    public static int Compare(string a, string b)
    {
        var (ea, ua, ra) = Split(a);
        var (eb, ub, rb) = Split(b);
        if (ea != eb) return ea.CompareTo(eb);
        var c = CompareFragment(ua, ub);
        return c != 0 ? c : CompareFragment(ra, rb);
    }

    private static (long Epoch, string Upstream, string Revision) Split(string v)
    {
        long epoch = 0;
        var colon = v.IndexOf(':');
        if (colon > 0 && long.TryParse(v[..colon], out var e)) { epoch = e; v = v[(colon + 1)..]; }
        var dash = v.LastIndexOf('-');
        return dash > 0 ? (epoch, v[..dash], v[(dash + 1)..]) : (epoch, v, "");
    }

    private static int Order(char c) => c == '~' ? -1 : char.IsAsciiLetter(c) ? c : c + 256;

    private static int CompareFragment(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            var firstDiff = 0;
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                var ac = i < a.Length && !char.IsAsciiDigit(a[i]) ? Order(a[i]) : 0;
                var bc = j < b.Length && !char.IsAsciiDigit(b[j]) ? Order(b[j]) : 0;
                if (ac != bc) return ac - bc;
                i++; j++;
            }
            while (i < a.Length && a[i] == '0') i++;
            while (j < b.Length && b[j] == '0') j++;
            while (i < a.Length && char.IsAsciiDigit(a[i]) && j < b.Length && char.IsAsciiDigit(b[j]))
            {
                if (firstDiff == 0) firstDiff = a[i] - b[j];
                i++; j++;
            }
            if (i < a.Length && char.IsAsciiDigit(a[i])) return 1;
            if (j < b.Length && char.IsAsciiDigit(b[j])) return -1;
            if (firstDiff != 0) return firstDiff;
        }
        return 0;
    }
}
