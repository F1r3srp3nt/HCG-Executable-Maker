using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.App.Services;

/// <summary>
/// HCG Store clients for Ship to Store. Authentication is the signed-in HCG Connect session (refreshed by
/// <see cref="HcgAccountService"/>); the app has no Store or storage credentials of its own.
/// </summary>
public sealed class StoreClientFactory(HcgAccountService account, ISettingsStore settings)
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"HCGExecutableMaker/{HostInfo.ProductVersion}");
        return h;
    }

    public async Task<Uri> OriginAsync(CancellationToken ct = default)
    {
        var configured = (await settings.LoadAsync(ct).ConfigureAwait(false)).HcgStoreUrl;
        return StoreOrigin.Parse(configured) ?? HcgStoreClient.DefaultOrigin;
    }

    public HcgStoreClient Create(Uri origin) =>
        new(Http, origin, account.GetAccessTokenAsync, account.RecoverFromUnauthorizedAsync);
}
