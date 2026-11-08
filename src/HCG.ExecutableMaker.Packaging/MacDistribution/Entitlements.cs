using System.Security;
using System.Text;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>An entitlement HCG proposes, with the source evidence that requires it.</summary>
public sealed record ProposedEntitlement(string Key, string Capability, string Evidence, string Explanation);

/// <summary>What the sources mean for one distribution channel.</summary>
public sealed record EntitlementPlan(
    IReadOnlyList<ProposedEntitlement> Proposed,
    IReadOnlyList<EntitlementEvidence> Reviews,
    IReadOnlyList<EntitlementEvidence> Blockers);

/// <summary>
/// Derives entitlements from source evidence only. Nothing is added without evidence, nothing the evidence requires is
/// dropped silently: the user confirms the plan, and a deselected entitlement is reported as a disabled feature.
/// </summary>
public static class Entitlements
{
    public const string AppSandbox = "com.apple.security.app-sandbox";
    public const string GetTaskAllow = "com.apple.security.get-task-allow";
    public const string ApplicationIdentifier = "com.apple.application-identifier";
    public const string TeamIdentifier = "com.apple.developer.team-identifier";

    /// <summary>Hardened runtime (Developer ID): only Resource Access entitlements the sources need.</summary>
    public static EntitlementPlan ForDeveloperId(IEnumerable<EntitlementEvidence> evidence)
    {
        var list = evidence.ToList();
        var proposed = list.Where(e => e.HardenedRuntimeEntitlement is not null)
            .GroupBy(e => e.HardenedRuntimeEntitlement!)
            .Select(g => new ProposedEntitlement(g.Key, g.First().Capability, g.First().Evidence, g.First().Explanation))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        // Sandbox-only concerns (running other programs, fixed paths, …) do not apply outside the sandbox.
        return new EntitlementPlan(proposed, [], []);
    }

    /// <summary>App Sandbox (Mac App Store): sandbox entitlements the sources need, plus items to review and blockers.</summary>
    public static EntitlementPlan ForAppStore(IEnumerable<EntitlementEvidence> evidence)
    {
        var list = evidence.ToList();
        var proposed = list.Where(e => e.SandboxEntitlement is not null && e.Kind == EntitlementEvidenceKind.RequiresEntitlement)
            .GroupBy(e => e.SandboxEntitlement!)
            .Select(g => new ProposedEntitlement(g.Key, g.First().Capability, g.First().Evidence, g.First().Explanation))
            .ToList();
        // read-write user-selected access supersedes read-only.
        if (proposed.Any(p => p.Key == "com.apple.security.files.user-selected.read-write"))
            proposed.RemoveAll(p => p.Key == "com.apple.security.files.user-selected.read-only");
        return new EntitlementPlan(
            proposed.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
            list.Where(e => e.Kind == EntitlementEvidenceKind.Review).ToList(),
            list.Where(e => e.Kind == EntitlementEvidenceKind.SandboxBlocker).ToList());
    }

    /// <summary>Entitlements a user deselected although the sources need them (shown as disabled functionality, never hidden).</summary>
    public static IReadOnlyList<ProposedEntitlement> Disabled(EntitlementPlan plan, IEnumerable<string> confirmed)
    {
        var set = confirmed.ToHashSet(StringComparer.Ordinal);
        return plan.Proposed.Where(p => !set.Contains(p.Key)).ToList();
    }

    /// <summary>Confirmed entitlements must come from the plan: HCG never signs an entitlement it has no evidence for.</summary>
    public static IReadOnlyList<string> Unexpected(EntitlementPlan plan, IEnumerable<string> confirmed) =>
        confirmed.Where(k => plan.Proposed.All(p => p.Key != k)).ToList();

    /// <summary>The XML entitlements plist (boolean true entitlements plus string values such as the application identifier).</summary>
    public static string ToPlist(IEnumerable<string> booleanKeys, IEnumerable<(string Key, string Value)>? stringValues = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
          .Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n")
          .Append("<plist version=\"1.0\">\n<dict>\n");
        var entries = booleanKeys.Distinct(StringComparer.Ordinal).Select(k => (k, (string?)null))
            .Concat((stringValues ?? []).Select(v => (v.Key, (string?)v.Value)))
            .OrderBy(e => e.Item1, StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            sb.Append("    <key>").Append(SecurityElement.Escape(key)).Append("</key>\n");
            sb.Append(value is null ? "    <true/>\n" : $"    <string>{SecurityElement.Escape(value)}</string>\n");
        }
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }
}
