using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;

namespace HCG.ExecutableMaker.App.Services;

/// <summary>
/// The staff-only publishing permission, decided by HCG Store (which asks HCG's central authority for its publisher
/// permission). Maker never decides it and never offers it for sale: a refusal points to the administrator.
/// </summary>
public sealed class StorePublishingAuthority(StoreClientFactory stores) : IStorePublishingAuthority
{
    public async Task<FeatureDecision> CheckAsync(MakerFeature feature, Uri? storeOrigin, CancellationToken ct)
    {
        var origin = storeOrigin ?? await stores.OriginAsync(ct).ConfigureAwait(false);
        var client = stores.Create(origin);
        try
        {
            var who = await client.WhoAmIAsync(ct).ConfigureAwait(false);
            var products = await client.ProductsAsync(ct).ConfigureAwait(false);
            return products.Count > 0
                ? new FeatureDecision(feature, FeatureAccessState.Allowed,
                    $"HCG Store at {origin.Host} allows {who.Email ?? who.Sub} to publish {products.Count} product(s) as private drafts. A person approves them in HCG Store.", Source: Source(origin))
                : StaffOnly(feature, $"Signed in to HCG Store at {origin.Host} as {who.Email ?? who.Sub}, but HCG has not given this account HCG Store's publishing permission for any product.", origin);
        }
        catch (HcgStoreException ex) { return Decide(feature, ex, origin); }
        // No answer (network failure or timeout): unknown, never a refusal and never a crash.
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(feature, FeatureAccessState.Unavailable,
                $"HCG Store at {origin.Host} did not answer ({(ex is OperationCanceledException ? "timed out" : ex.Message)}). This is not a refusal.", FeatureRemedy.TryAgain, Source: Source(origin));
        }
    }

    private static string Source(Uri origin) => $"HCG Store ({origin.Host})";

    internal static FeatureDecision Decide(MakerFeature feature, HcgStoreException ex, Uri origin) => (ex.Status, ex.Code) switch
    {
        (401, _) or (_, "sign_in_required") => new(feature, FeatureAccessState.SignInRequired,
            "HCG Store did not accept your HCG session. Sign in with HCG again.", FeatureRemedy.SignIn, Source: Source(origin)),
        (403, _) => StaffOnly(feature, ex.Message, origin),
        (404, _) => new(feature, FeatureAccessState.NotAvailableYet,
            $"HCG Store at {origin.Host} does not answer publishing requests (HTTP 404: {ex.Message}). Publishing is off there, or the HCG Store server is not running at that address.",
            FeatureRemedy.WaitForHcg, Source: Source(origin)),
        _ => new(feature, FeatureAccessState.Unavailable,
            $"HCG Store at {origin.Host} could not decide right now ({ex.Message}). This is not a refusal.", FeatureRemedy.TryAgain, Source: Source(origin)),
    };

    private static FeatureDecision StaffOnly(MakerFeature feature, string why, Uri origin) => new(feature, FeatureAccessState.NotPermitted,
        why + " Publishing is a staff permission granted by HCG.", FeatureRemedy.AskAdministrator, Source: Source(origin));
}
