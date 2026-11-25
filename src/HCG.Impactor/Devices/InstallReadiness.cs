using HCG.ExecutableMaker.Domain;
using HCG.Impactor.Model;

namespace HCG.Impactor.Devices;

/// <summary>
/// Pre-installation validation of a signed IPA for one device: the device checks (<see cref="DeviceCompatibility"/>)
/// plus the signing facts the device will enforce — a real (non-ad hoc) signature, a valid certificate, an embedded
/// profile that is valid now, installable, lists the device and matches the app's team and identifier.
/// </summary>
public static class InstallReadiness
{
    public static IReadOnlyList<Finding> Evaluate(IpaReport ipa, AppleDevice device, DateTimeOffset now)
    {
        var f = new List<Finding>(DeviceCompatibility.Evaluate(ipa, device));
        f.AddRange(ipa.Findings.Where(x => x.Severity == FindingSeverity.Blocker));
        var app = ipa.App;
        if (app is null) return f;

        if (!app.Signature.Present || app.Signature.AdHoc)
        {
            f.Add(Finding.Block("install.not-signed", "Not signed for devices", app.Signature.Present ? "The app has an ad hoc signature." : "The app is unsigned.",
                "Sign it in Signing Studio first."));
            return f;
        }
        if (app.Signature.Signer is { } cert && cert.NotAfter <= now)
            f.Add(Finding.Block("install.certificate-expired", "Signing certificate expired", $"{cert.CommonName} expired on {cert.NotAfter:yyyy-MM-dd}.", "Re-sign with a current certificate."));

        foreach (var b in ipa.AllBundles.Where(b => b.TakesEntitlements))
        {
            var label = b.RelativePath.Length == 0 ? "The app" : b.RelativePath;
            var p = b.EmbeddedProfile?.Profile;
            if (p is null)
            {
                f.Add(Finding.Block("install.no-profile", "No provisioning profile", $"{label} has no embedded.mobileprovision; iOS refuses to install it.", "Sign it in Signing Studio with a profile.") with { Subject = label });
                continue;
            }
            if (p.Expiration is { } exp && exp <= now)
                f.Add(Finding.Block("install.profile-expired", "Provisioning profile expired", $"{label}: \"{p.Name}\" expired on {exp:yyyy-MM-dd}.", "Re-sign with a current profile.") with { Subject = label });
            if (p.Kind == AppleProfileKind.AppStore)
                f.Add(Finding.Block("install.app-store-profile", "App Store profile", $"{label} is signed for App Store distribution; devices install it only from the App Store or TestFlight.",
                    "Sign with a Development or Ad Hoc profile.") with { Subject = label });
            if (p.Kind is AppleProfileKind.Development or AppleProfileKind.AdHoc)
            {
                if (device.Udid is null)
                    f.Add(Finding.Warn("install.registration-unknown", "Device registration not checked", $"{device.Name} did not report its UDID.") with { Subject = label });
                else if (!p.ProvisionedDevices.Contains(device.Udid, StringComparer.OrdinalIgnoreCase))
                    f.Add(Finding.Block("device.unregistered", "Device not registered in the profile",
                        $"{device.Name} is not among the {p.ProvisionedDevices.Count} devices of \"{p.Name}\".",
                        "Register the device at developer.apple.com › Devices, regenerate the profile, import it and re-sign.") with { Subject = label });
            }
            if (b.Signature.TeamId is { } team && p.TeamIds.Count > 0 && !p.TeamIds.Contains(team))
                f.Add(Finding.Block("install.team-mismatch", "Profile team differs from the signature", $"{label} is signed by team {team} but embeds a profile of {string.Join(", ", p.TeamIds)}.") with { Subject = label });
            if (b.BundleIdentifier is { } id && !Signing.SigningPlanner.AppIdCovers(p.ApplicationIdentifier, p.TeamIds.FirstOrDefault(), id))
                f.Add(Finding.Block("install.app-id-mismatch", "Profile does not cover the bundle identifier", $"{label} is {id}; its profile is for {p.ApplicationIdentifier}.") with { Subject = label });
        }
        return f;
    }
}
