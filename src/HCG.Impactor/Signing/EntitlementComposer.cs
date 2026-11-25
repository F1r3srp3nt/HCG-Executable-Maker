using HCG.ExecutableMaker.Packaging.Signing;
using HCG.Impactor.Model;

namespace HCG.Impactor.Signing;

/// <summary>
/// Works out the entitlements a bundle is signed with. Starting point: what the app itself requests (its current
/// signature's entitlements), with the team prefix and bundle identifier moved to the new signer. Every requested
/// entitlement must be granted by the provisioning profile — exactly, by "*", or by a granted "PREFIX.*". The
/// identity entitlements (application-identifier, team identifier, get-task-allow, aps-environment) always come from
/// the profile. Nothing is ever added that the profile does not grant.
/// </summary>
public static class EntitlementComposer
{
    private static readonly HashSet<string> FromProfile = new(StringComparer.Ordinal)
    {
        "application-identifier", "com.apple.developer.team-identifier", "get-task-allow", "aps-environment", "com.apple.developer.aps-environment",
        "beta-reports-active",
    };

    public static (IReadOnlyDictionary<string, object> Entitlements, IReadOnlyList<string> Notes, IReadOnlyList<Finding> Findings) Compose(
        BundleInfo bundle, ProfileEntry profile, string finalBundleId, bool dropUnsupported)
    {
        var granted = profile.RawEntitlements;
        var team = profile.Team ?? "";
        var oldTeam = bundle.Signature.TeamId ?? (bundle.Signature.RawEntitlements.GetValueOrDefault("com.apple.developer.team-identifier") as string);
        var oldId = bundle.BundleIdentifier ?? finalBundleId;
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var notes = new List<string>();
        var findings = new List<Finding>();

        var appId = $"{team}.{finalBundleId}";
        result["application-identifier"] = appId;
        if (granted.ContainsKey("com.apple.developer.team-identifier") || team.Length > 0) result["com.apple.developer.team-identifier"] = team;
        if (granted.GetValueOrDefault("get-task-allow") is bool gta) result["get-task-allow"] = gta;
        foreach (var aps in new[] { "aps-environment", "com.apple.developer.aps-environment" })
            if (bundle.Signature.RawEntitlements.ContainsKey(aps))
            {
                if (granted.GetValueOrDefault(aps) is string env) { result[aps] = env; notes.Add($"{aps} = {env} (from the profile)"); }
                else Unsupported(aps, "push notifications are not enabled for this App ID");
            }

        foreach (var (key, original) in bundle.Signature.RawEntitlements)
        {
            if (FromProfile.Contains(key)) continue;
            var value = Rewrite(original, oldTeam, team, oldId, finalBundleId);
            if (!granted.TryGetValue(key, out var allowed)) { Unsupported(key, "the profile does not grant it"); continue; }
            var requestedText = AppleCertificates.RenderValue(value);
            var grantedText = AppleCertificates.RenderValue(Rewrite(allowed, null, team, null, null));
            if (value is bool b) { if (allowed is bool ab && (ab || !b)) result[key] = value; else Unsupported(key, $"the profile allows {grantedText}"); continue; }
            if (SigningValidator.ValueAllowed(requestedText, grantedText)) result[key] = value;
            else Unsupported(key, $"{requestedText} is not within the profile's {grantedText}");
        }

        // An app that had no entitlements (unsigned/ad hoc build) still needs its keychain group when the profile grants groups.
        if (!result.ContainsKey("keychain-access-groups") && granted.GetValueOrDefault("keychain-access-groups") is List<object> groups
            && groups.OfType<string>().Any(g => SigningValidator.ValueAllowed(appId, g.Replace("$(AppIdentifierPrefix)", team + "."))))
        {
            result["keychain-access-groups"] = new List<object> { appId };
            notes.Add($"keychain-access-groups = {appId} (the app's own group)");
        }
        return (result, notes, findings);

        void Unsupported(string key, string why)
        {
            if (dropUnsupported)
            {
                notes.Add($"Removed {key}: {why}.");
                findings.Add(Finding.Warn("entitlement.removed", "Entitlement removed", $"{key} was removed because {why}. Features that depend on it will not work.",
                    "Enable the capability for the App ID at developer.apple.com and regenerate the profile to keep it."));
            }
            else
                findings.Add(Finding.Block("entitlement.not-granted", "Entitlement not allowed by the profile", $"{key}: {why}.",
                    "Enable the capability for this App ID at developer.apple.com › Identifiers and regenerate the profile — or choose \"Remove unsupported entitlements\" in Advanced options."));
        }
    }

    /// <summary>Moves team prefixes and the bundle identifier inside entitlement values (strings and string arrays).</summary>
    internal static object Rewrite(object value, string? oldTeam, string newTeam, string? oldId, string? newId) => value switch
    {
        string s => RewriteString(s, oldTeam, newTeam, oldId, newId),
        List<object> list => list.Select(v => Rewrite(v, oldTeam, newTeam, oldId, newId)).ToList(),
        _ => value,
    };

    private static string RewriteString(string s, string? oldTeam, string newTeam, string? oldId, string? newId)
    {
        s = s.Replace("$(AppIdentifierPrefix)", newTeam + ".", StringComparison.Ordinal).Replace("$(TeamIdentifierPrefix)", newTeam + ".", StringComparison.Ordinal);
        if (oldTeam is not null && oldTeam != newTeam && s.StartsWith(oldTeam + ".", StringComparison.Ordinal)) s = newTeam + s[oldTeam.Length..];
        if (oldId is not null && newId is not null && oldId != newId)
        {
            var prefix = newTeam + "." + oldId;
            if (s == prefix || s.StartsWith(prefix + ".", StringComparison.Ordinal)) s = newTeam + "." + newId + s[prefix.Length..];
        }
        return s;
    }
}
