using System.Text.Json;
using HCG.ExecutableMaker.Domain;
using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Ecosystem;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;
using HCG.Impactor.Storage;

namespace HCG.Impactor.Cli;

/// <summary>
/// HCG Impactor's command line (<c>HCGExecutableMaker --impactor &lt;command&gt; …</c>). The same service the workspace
/// uses; every command prints what it checked and why. Exit codes: 0 done, 1 blocked or failed, 2 usage.
/// Nothing is uninstalled, published or uploaded; signing writes a new IPA and never touches the original.
/// </summary>
public sealed class ImpactorCli(ImpactorService service, TextWriter output, string appVersion, string installationId)
{
    public const string Usage = """
        HCG Impactor — inspect, sign and install iOS/iPadOS apps on your own devices.

        Usage: HCGExecutableMaker --impactor <command> [options]

          inspect <ipa|app> [--json]                 Inspection report (identity, versions, architectures, signatures,
                                                     entitlements, profiles, certificates, integrity). Read-only.
          identities                                 Signing identities in this Mac's keychain (iOS: Apple Development/Distribution).
          profiles [--ipa <ipa>]                     Installed provisioning profiles (expiry, type, App ID, devices);
                                                     with --ipa: which profiles fit each bundle.
          devices [--json] [--include-udid]          Connected and paired iPhones/iPads (hcg.apple-device/1 with --json).
          plan <ipa> [signing options]               What signing would do: identity, profiles, entitlements, blockers.
          sign <ipa> [--out <new.ipa>] [signing options]
                                                     Sign or re-sign into a NEW IPA (verified with codesign, re-inspected).
          check <signed.ipa> --device <id|name|usb>  Pre-installation validation for a device (no changes).
          install <ipa> --device <id|name|usb>... [--launch] [--yes] [--sign [signing options]]
                                                     Validate and install (optionally sign first); several --device = batch.
          diagnose <file|->                          Explain an installation error output (redacted).
          history | monitor                          Installation history · signing expiry reminders.
          open-in-system-tester --device <id|name|usb>
                                                     Open HCG System Tester's Connected Systems with this device's record.

        Signing options:
          --identity <SHA-1>          Keychain identity (default: the best match for the profiles).
          --profile <UUID>            Profile for the main app (default: automatic).
          --profile-file <path>       Use this .mobileprovision as well as the installed ones (repeatable).
          --extension-profile <relative path>=<UUID>   Profile for a nested bundle, e.g. PlugIns/Share.appex=<UUID>.
          --method development|adhoc|enterprise|appstore
          --bundle-id <id>            New bundle identifier (installs as a separate app — warned).
          --device <id|name|usb>      Check the profile's device list for this device.
          --drop-unsupported          Remove entitlements the profile does not grant (each removal reported).
          --report <folder>           Also export diagnostics (hcg.diagnostic-report/1 + Markdown) there.
        """;

    private string[] _args = [];
    private string? Arg(string name) => _args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    private List<string> All(string name) => _args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < _args.Length).Select(x => _args[x.i + 1]).ToList();
    private bool Flag(string name) => _args.Contains(name);
    private void W(string s = "") => output.WriteLine(s);

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        _args = args;
        var cmd = args.FirstOrDefault();
        var target = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var caps = service.Capabilities;
        W($"HCG Impactor · {caps.Platform} · sign: {(caps.CanSign ? "yes" : "no")} · install: {(caps.CanInstall ? "yes" : "no")}" + (caps.Limitation is null ? "" : $"\n  {caps.Limitation}"));
        try
        {
            return cmd switch
            {
                "inspect" when target is not null => Inspect(target),
                "identities" => await IdentitiesAsync(ct),
                "profiles" => await ProfilesAsync(ct),
                "devices" => await DevicesAsync(ct),
                "plan" when target is not null => await PlanAsync(target, sign: false, ct),
                "sign" when target is not null => await PlanAsync(target, sign: true, ct),
                "check" when target is not null => await CheckAsync(target, ct),
                "install" when target is not null => await InstallAsync(target, ct),
                "diagnose" when target is not null => Diagnose(target),
                "history" => History(),
                "monitor" => Monitor(),
                "open-in-system-tester" => await OpenInSystemTesterAsync(ct),
                _ => UsageError(),
            };
        }
        catch (OperationCanceledException) { W("Cancelled."); return 1; }
    }

    private int UsageError() { W(Usage); return 2; }

    // ---------------------------------------------------------------- inspect

    private int Inspect(string path)
    {
        var r = service.Inspect(path);
        if (Flag("--json")) { W(JsonSerializer.Serialize(Summary(r), new JsonSerializerOptions { WriteIndented = true })); return r.CanProceed ? 0 : 1; }
        PrintInspection(r);
        return r.App is not null && !r.Findings.HasBlockers() ? 0 : 1;
    }

    private void PrintInspection(IpaReport r)
    {
        W($"\nIPA: {r.SourcePath}");
        if (r.Sha256 is not null) W($"  Size {r.SizeBytes:N0} bytes · {r.FileCount:N0} files · SHA-256 {r.Sha256}");
        if (r.App is { } a)
        {
            W($"  App: {a.Name} · {a.BundleIdentifier}");
            W($"  Version {a.ShortVersion} (build {a.BuildNumber}) · minimum iOS {a.MinimumOSVersion ?? "not stated"} · devices {r.Families}");
            W($"  Architectures: {string.Join(", ", a.Slices.Select(s => s.Architecture + (s.Platform is null ? "" : $" [{s.Platform}]") + (s.Encrypted ? " ENCRYPTED" : "")))}");
            if (r.RequiredCapabilities.Count > 0) W($"  Required capabilities: {string.Join(", ", r.RequiredCapabilities)}");
            PrintSignature("  ", a);
        }
        foreach (var b in r.Nested)
        {
            W($"\n  {b.Kind}: {b.RelativePath}" + (b.BundleIdentifier is null ? "" : $" · {b.BundleIdentifier}") + (b.ExtensionPoint is null ? "" : $" · {b.ExtensionPoint}"));
            W($"    {string.Join(", ", b.Architectures)}" + (b.MinimumOSVersion is null ? "" : $" · iOS {b.MinimumOSVersion}+"));
            PrintSignature("    ", b);
        }
        PrintFindings(r.Findings);
    }

    private void PrintSignature(string pad, BundleInfo b)
    {
        var s = b.Signature;
        W(pad + "Signature: " + (!s.Present ? "unsigned" : s.AdHoc ? "ad hoc (no certificate)" :
            $"{s.Signer?.CommonName} · team {s.TeamId} · expires {s.Signer?.NotAfter:yyyy-MM-dd}" + (s.SigningTime is { } t ? $" · signed {t:yyyy-MM-dd HH:mm}" : "")) + (s.HashType is null ? "" : $" · {s.HashType}"));
        if (s.Chain.Count > 1) W(pad + "  Chain: " + string.Join(" ← ", s.Chain));
        if (s.Entitlements.Count > 0) W(pad + "  Entitlements: " + string.Join("; ", s.Entitlements.Select(kv => $"{kv.Key}={kv.Value}")));
        if (b.EmbeddedProfile is { } ep)
            W(pad + "  Profile: " + (ep.Profile is { } p ? $"\"{p.Name}\" · {p.Kind} · {p.ApplicationIdentifier} · expires {p.Expiration:yyyy-MM-dd} · {p.ProvisionedDevices.Count} device(s) · {p.Signature}" : ep.Problem));
        if (b.Resources.Present) W(pad + $"  Sealed resources: {b.Resources.Verified}/{b.Resources.Sealed} verified" + (b.Resources.Intact ? "" : $", {b.Resources.Modified.Count} modified, {b.Resources.Missing.Count} missing"));
    }

    private void PrintFindings(IEnumerable<Finding> findings)
    {
        var list = findings.ToList();
        if (list.Count == 0) { W("\n  No problems found."); return; }
        W("");
        foreach (var f in list.OrderByDescending(f => f.Severity))
        {
            var tag = f.Severity switch { FindingSeverity.Blocker => "BLOCKER", FindingSeverity.Warning => "WARNING", _ => "info" };
            W($"  [{tag}] {f.Title}{(f.Subject is null ? "" : $" ({f.Subject})")}: {f.Detail}");
            if (f.Remedy is not null) W($"           → {f.Remedy}");
        }
    }

    private static object Summary(IpaReport r) => new
    {
        r.SourcePath, r.Sha256, r.SizeBytes, r.FileCount, Families = r.Families.ToString(), r.RequiredCapabilities,
        Bundles = r.AllBundles.Select(b => new
        {
            b.RelativePath, Kind = b.Kind.ToString(), b.BundleIdentifier, b.Name, b.ShortVersion, b.BuildNumber, b.MinimumOSVersion,
            Architectures = b.Architectures, Encrypted = b.IsEncrypted,
            Signature = new { b.Signature.Present, b.Signature.AdHoc, b.Signature.TeamId, b.Signature.Identifier, Signer = b.Signature.Signer?.CommonName, SignerExpires = b.Signature.Signer?.NotAfter, b.Signature.SigningTime, b.Signature.Entitlements },
            Profile = b.EmbeddedProfile?.Profile is { } p ? new { p.Name, p.Uuid, Kind = p.Kind.ToString(), p.ApplicationIdentifier, p.Expiration, Devices = p.ProvisionedDevices.Count, Signature = p.Signature.ToString() } : null,
            Resources = new { b.Resources.Present, b.Resources.Sealed, b.Resources.Verified, b.Resources.Modified, b.Resources.Missing },
        }),
        Findings = r.Findings.Select(f => new { Severity = f.Severity.ToString(), f.Code, f.Title, f.Detail, f.Remedy, f.Subject }),
    };

    // ---------------------------------------------------------------- inventory

    private async Task<int> IdentitiesAsync(CancellationToken ct)
    {
        var inv = await service.ReadInventoryAsync(null, ct);
        var now = service.Time.GetUtcNow();
        W("\nSigning identities (certificate + private key in this Mac's keychain):");
        var ios = inv.Certificates.Where(c => c.Kind is AppleCertificateKind.Development or AppleCertificateKind.AppleDistribution).ToList();
        if (ios.Count == 0) W("  None. Create one in Xcode › Settings › Accounts › Manage Certificates (Apple Development).");
        foreach (var c in ios)
            W($"  {c.Sha1}  {c.CommonName} · team {c.TeamId} · expires {c.NotAfter:yyyy-MM-dd} ({(int)(c.NotAfter - now).TotalDays} days) · {c.Readiness(now)}" + (c.TrustProblem is null ? "" : $" ({c.TrustProblem})"));
        if (service.Inventory.LastProblem is { } p) W("  Note: " + p);
        return 0;
    }

    private async Task<int> ProfilesAsync(CancellationToken ct)
    {
        var inv = await service.ReadInventoryAsync(All("--profile-file"), ct);
        var now = service.Time.GetUtcNow();
        W("\nProvisioning profiles:");
        if (inv.Profiles.Count == 0) W("  None installed. Xcode › Settings › Accounts › Download Manual Profiles, or developer.apple.com › Profiles.");
        foreach (var p in inv.Profiles.OrderBy(p => p.Info.Expiration))
            W($"  {p.Info.Uuid}  \"{p.Info.Name}\" · {p.Info.Kind} · {p.Info.ApplicationIdentifier} · expires {p.Info.Expiration:yyyy-MM-dd}" +
              $"{(p.Info.Expiration <= now ? " (EXPIRED)" : "")} · {p.Info.ProvisionedDevices.Count} device(s){(p.Info.ProvisionsAllDevices ? " (all)" : "")} · {p.Info.CertificateSha1s.Count} cert(s) · {p.Info.Signature}");
        if (Arg("--ipa") is { } ipaPath)
        {
            var ipa = service.Inspect(ipaPath);
            W($"\nFit for {Path.GetFileName(ipaPath)}:");
            foreach (var b in ipa.AllBundles.Where(b => b.TakesEntitlements && b.BundleIdentifier is not null))
            {
                var fits = inv.Profiles.Where(p => SigningPlanner.AppIdCovers(p.Info.ApplicationIdentifier, p.Team, b.BundleIdentifier!) && p.Info.Expiration > now).ToList();
                W($"  {(b.RelativePath.Length == 0 ? "App" : b.RelativePath)} ({b.BundleIdentifier}): " + (fits.Count == 0 ? "no valid profile covers this App ID" : string.Join(", ", fits.Select(p => $"\"{p.Info.Name}\" ({p.Method})"))));
            }
        }
        if (service.Inventory.LastProblem is { } prob) W("  Note: " + prob);
        return 0;
    }

    // ---------------------------------------------------------------- devices

    private async Task<int> DevicesAsync(CancellationToken ct)
    {
        var listing = await service.ListDevicesAsync(ct);
        if (Flag("--json")) { W(AppleDeviceContract.Serialize(listing.Devices, "HCG Impactor CLI", service.Time.GetUtcNow(), Flag("--include-udid"))); return listing.Problem is null ? 0 : 1; }
        W($"\nDevices (devicectl {listing.ToolVersion ?? "?"}):");
        if (listing.Problem is { } p) { PrintDiagnosis(p); return 1; }
        if (listing.Devices.Count == 0) W("  No iPhone or iPad is paired with or connected to this Mac. Connect one by cable, unlock it and tap Trust.");
        foreach (var d in listing.Devices) PrintDevice(d);
        return 0;
    }

    private void PrintDevice(AppleDevice d)
    {
        W($"  {d.Name} — {d.DisplayModel} ({d.ProductType}) · {d.Platform} {d.OsVersion} ({d.OsBuild}) · {d.CpuArchitecture}");
        W($"    id {d.Identifier} · UDID {(d.Udid is null ? "not reported" : AppleDeviceContract.Redact(d.Udid))} · {d.Connection}{(d.Transport is null ? "" : $" ({d.Transport})")} · tunnel {d.TunnelState ?? "?"}");
        W($"    pairing {d.Pairing} · Developer Mode {d.DeveloperMode} · developer services {(d.DeveloperServicesAvailable is null ? "?" : d.DeveloperServicesAvailable.Value ? "available" : "not available")} · install {(d.CanInstall ? "yes" : "no")} · launch {(d.CanLaunch ? "yes" : "no")}");
    }

    private async Task<List<AppleDevice>?> ResolveDevicesAsync(CancellationToken ct)
    {
        var wanted = All("--device");
        if (wanted.Count == 0) return [];
        var listing = await service.ListDevicesAsync(ct);
        if (listing.Problem is { } p) { PrintDiagnosis(p); return null; }
        var result = new List<AppleDevice>();
        foreach (var w in wanted)
        {
            var match = w == "usb"
                ? listing.Devices.Where(d => d.Connection == DeviceConnection.Usb).ToList()
                : listing.Devices.Where(d => d.Identifier.Equals(w, StringComparison.OrdinalIgnoreCase) || (d.Udid?.Equals(w, StringComparison.OrdinalIgnoreCase) ?? false)
                    || d.Name.Equals(w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count != 1)
            {
                W(match.Count == 0 ? $"No device matches \"{w}\". Run: --impactor devices" : $"\"{w}\" matches {match.Count} devices; use the identifier.");
                return null;
            }
            result.Add(match[0]);
        }
        return result;
    }

    // ---------------------------------------------------------------- plan / sign

    private SigningRequest Request(AppleDevice? device) => new()
    {
        IdentitySha1 = Arg("--identity"),
        ProfileUuid = Arg("--profile"),
        ProfileOverrides = All("--extension-profile").Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]),
        Method = Arg("--method") switch
        {
            "development" => DistributionMethod.Development, "adhoc" or "ad-hoc" => DistributionMethod.AdHoc,
            "enterprise" => DistributionMethod.Enterprise, "appstore" or "app-store" => DistributionMethod.AppStore, _ => null,
        },
        NewBundleIdentifier = Arg("--bundle-id"),
        TargetDevice = device,
        DropUnsupportedEntitlements = Flag("--drop-unsupported"),
    };

    private async Task<(IpaReport Ipa, SigningPlan Plan, AppleDevice? Device)?> PreparePlanAsync(string ipaPath, CancellationToken ct)
    {
        var devices = await ResolveDevicesAsync(ct);
        if (devices is null) return null;
        var device = devices.FirstOrDefault();
        var ipa = service.Inspect(ipaPath);
        PrintInspection(ipa);
        var inv = await service.ReadInventoryAsync(All("--profile-file"), ct);
        var plan = service.Plan(ipa, inv, Request(device));
        PrintPlan(plan);
        return (ipa, plan, device);
    }

    private void PrintPlan(SigningPlan plan)
    {
        W("\nSigning plan:");
        if (plan.Identity is { } id) W($"  Identity: {id.CommonName} ({id.Sha1}) · expires {id.NotAfter:yyyy-MM-dd}");
        if (plan.Method is { } m) W($"  Method: {m}{(plan.Installable ? "" : " (not installable on devices)")} · signed app valid until {plan.ExpiresAt:yyyy-MM-dd}");
        foreach (var a in plan.Assignments)
        {
            if (a.Profile is null) { W($"  {a.Label}: signed without entitlements ({a.Bundle.Kind})"); continue; }
            W($"  {a.Label}: {a.FinalBundleIdentifier} · profile \"{a.Profile.Info.Name}\" ({a.Profile.Info.Uuid})");
            if (a.Entitlements is { } e) W("    Entitlements: " + string.Join("; ", e.Select(kv => $"{kv.Key}={HCG.ExecutableMaker.Packaging.Signing.AppleCertificates.RenderValue(kv.Value)}")));
            foreach (var n in a.Notes) W("    " + n);
        }
        PrintFindings(plan.Findings);
        W(plan.IsReady ? "\n  Ready to sign." : "\n  Not ready: resolve the blockers above.");
    }

    private async Task<int> PlanAsync(string ipaPath, bool sign, CancellationToken ct)
    {
        var prepared = await PreparePlanAsync(ipaPath, ct);
        if (prepared is not { } p) return 1;
        var session = new ImpactorSession { Inspection = p.Ipa, Plan = p.Plan, Device = p.Device };
        if (!sign || !p.Plan.IsReady) { Report(session); return p.Plan.IsReady ? 0 : 1; }
        var result = await SignAsync(ipaPath, p.Ipa, p.Plan, ct);
        Report(session with { Signing = result });
        if (result.Succeeded) service.Signer.Cleanup(result);
        return result.Succeeded ? 0 : 1;
    }

    private async Task<SignResult> SignAsync(string ipaPath, IpaReport ipa, SigningPlan plan, CancellationToken ct)
    {
        W("\nSigning:");
        var result = await service.SignAsync(ipaPath, ipa, plan, Arg("--out"), new Progress<string>(l => W("  " + l)), ct);
        if (result.Succeeded)
        {
            W($"\n  Signed IPA: {result.OutputIpa}\n  SHA-256: {result.OutputSha256}\n  codesign --verify: {result.Verification?.Split('\n').LastOrDefault()}");
            if (result.Reinspection?.App is { } app)
                W($"  Re-inspected: {app.BundleIdentifier} signed by {app.Signature.Signer?.CommonName} (team {app.Signature.TeamId}), profile \"{app.EmbeddedProfile?.Profile?.Name}\"");
        }
        else if (result.Failure is { } f) PrintDiagnosis(f);
        return result;
    }

    // ---------------------------------------------------------------- check / install

    private async Task<int> CheckAsync(string ipaPath, CancellationToken ct)
    {
        var devices = await ResolveDevicesAsync(ct);
        if (devices is null || devices.Count == 0) { if (devices is not null) W("Name a device with --device (see: --impactor devices)."); return devices is null ? 1 : 2; }
        var ipa = service.Inspect(ipaPath);
        var blocked = false;
        foreach (var d in devices)
        {
            W($"\nPre-installation check for {d.Name}:");
            var (checks, existing) = await service.CheckAsync(ipa, d, ct);
            if (existing is not null) W($"  Installed now: {existing.Name} {existing.Version} ({existing.Build})");
            PrintFindings(checks);
            blocked |= checks.HasBlockers();
        }
        return blocked ? 1 : 0;
    }

    private async Task<int> InstallAsync(string ipaPath, CancellationToken ct)
    {
        var devices = await ResolveDevicesAsync(ct);
        if (devices is null) return 1;
        if (devices.Count == 0) { W("Name at least one device with --device (see: --impactor devices)."); return 2; }
        var ipa = ipaPath;
        var session = new ImpactorSession();
        SignResult? signed = null;
        if (Flag("--sign"))
        {
            var inspection = service.Inspect(ipaPath);
            PrintInspection(inspection);
            var inv = await service.ReadInventoryAsync(All("--profile-file"), ct);
            var plan = service.Plan(inspection, inv, Request(devices[0]));
            PrintPlan(plan);
            session = session with { Inspection = inspection, Plan = plan, Device = devices[0] };
            if (!plan.IsReady) { Report(session); return 1; }
            signed = await SignAsync(ipaPath, inspection, plan, ct);
            session = session with { Signing = signed };
            if (!signed.Succeeded) { Report(session); return 1; }
            ipa = signed.OutputIpa!;
        }
        var ok = true;
        foreach (var d in devices)
        {
            W($"\nInstalling on {d.Name} ({d.Connection}) …");
            var item = new DeploymentItem(ipa, d) { LaunchAfterInstall = Flag("--launch"), WarningsAcknowledged = Flag("--yes") };
            var r = await service.DeployAsync(item, new Progress<InstallProgress>(p => W($"  {(p.Percent is { } pct ? pct + "% " : "")}{p.Message}")), ct);
            PrintFindings(r.Checks);
            if (r.Outcome == InstallOutcome.Blocked && !r.Checks.HasBlockers())
                W("\n  Review the warnings above; run again with --yes to install anyway.");
            W($"\n  Result: {r.Outcome} — {r.Summary}");
            if (r.Install?.Failure is { } f) PrintDiagnosis(f);
            if (r.Launch?.Failure is { } lf) PrintDiagnosis(lf);
            session = session with { Device = d, DeviceFindings = r.Checks, Install = r.Install, Launch = r.Launch, Inspection = session.Inspection ?? r.Inspection };
            ok &= r.Outcome == InstallOutcome.Installed;
        }
        Report(session);
        if (signed is not null) service.Signer.Cleanup(signed);
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- misc

    private int Diagnose(string source)
    {
        var text = source == "-" ? Console.In.ReadToEnd() : File.ReadAllText(source);
        PrintDiagnosis(InstallDiagnostics.Diagnose(text));
        return 0;
    }

    private void PrintDiagnosis(Diagnosis d)
    {
        W($"\n  {d.Title} [{d.Kind}]");
        W($"    {d.Explanation}");
        W($"    → {d.Remedy}");
        if (d.Evidence is not null) W($"    Evidence: {d.Evidence}");
        if (d.AlsoMentioned.Count > 0) W($"    Also mentioned: {string.Join(", ", d.AlsoMentioned)}");
        if (d.RawRedacted.Length > 0) W("    Original output (redacted):\n" + string.Join("\n", d.RawRedacted.Split('\n').TakeLast(40).Select(l => "      " + l)));
    }

    private int History()
    {
        var h = service.Store.History();
        W($"\nInstallation history ({h.Count}):");
        foreach (var r in h.Take(50)) W($"  {r.At:yyyy-MM-dd HH:mm} · {r.Outcome} · {r.AppName} {r.Version} → {r.DeviceName} ({r.DeviceOs}, {r.Connection}) · {r.Summary}");
        return 0;
    }

    private int Monitor()
    {
        var now = service.Time.GetUtcNow();
        var apps = service.Store.SignedApps();
        W($"\nSigned apps ({apps.Count}):");
        foreach (var a in apps) W($"  {a.AppName} {a.Version} · {a.BundleIdentifier} · signed {a.SignedAt:yyyy-MM-dd} · {a.Method} · valid until {a.ExpiresAt:yyyy-MM-dd} · installed on {(a.InstalledOn.Count == 0 ? "—" : string.Join(", ", a.InstalledOn))}");
        var reminders = service.Store.Reminders(now);
        W(reminders.Count == 0 ? "\nNo signatures expire in the next 14 days." : "\nReminders:");
        foreach (var r in reminders) W($"  [{r.Level}] {r.Message}");
        W("\nApple does not allow permanent signing or background re-signing: re-sign and reinstall manually before the date shown.");
        return 0;
    }

    private async Task<int> OpenInSystemTesterAsync(CancellationToken ct)
    {
        var devices = await ResolveDevicesAsync(ct);
        if (devices is not { Count: 1 }) { W("Name exactly one device with --device."); return 2; }
        var record = HcgLinks.DeviceHandoff(devices[0], service.Time.GetUtcNow());
        var file = Path.Combine(service.Store.Directory, "system-tester-device.json");
        Directory.CreateDirectory(service.Store.Directory);
        await File.WriteAllTextAsync(file, record, ct);
        W($"\nDevice record ({AppleDeviceContract.Schema}) written to {file}:\n{record}");
        W($"\nOpen: {HcgLinks.SystemTesterConnectedSystems}");
        if (!OperatingSystem.IsMacOS()) { W("HCG System Tester runs on macOS."); return 1; }
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/open", [HcgLinks.SystemTesterConnectedSystems.ToString()]) { RedirectStandardError = true });
        await p!.WaitForExitAsync(ct);
        if (p.ExitCode != 0) { W("HCG System Tester is not installed on this Mac (no app handles hcgsystemtester://)."); return 1; }
        return 0;
    }

    private void Report(ImpactorSession session)
    {
        if (Arg("--report") is not { } folder) return;
        var dir = service.ExportDiagnostics(session, Path.GetFullPath(folder), appVersion, installationId);
        W($"\nDiagnostics exported: {dir} (report.json — {ImpactorReport.Schema}; report.md)");
    }
}
