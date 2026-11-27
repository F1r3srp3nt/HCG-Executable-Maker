namespace HCG.ExecutableMaker.Account.Store;

/// <summary>
/// Which HCG Store may receive which HCG Connect session. A sign-in from a local TEST identity server is only ever sent
/// to a Store on this computer (a TEST Store), and a sign-in from a real HCG Connect (production or development) only to
/// a Store that is not on this computer. So a production access token never reaches a local TEST server, and a TEST
/// token never reaches the production Store, even when the Store address is pointed there by mistake.
/// </summary>
public static class StoreSessionPolicy
{
    public static bool IsLocal(Uri origin) => origin.IsLoopback || origin.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Null when the session may be sent to <paramref name="origin"/>; otherwise why it is not.</summary>
    public static string? Refusal(HcgConnectConfiguration connect, Uri origin)
    {
        var testSession = connect.UsesLocalIdentityServer;
        var localStore = IsLocal(origin);
        if (testSession == localStore) return null;
        return testSession
            ? $"Your sign-in is from a local TEST identity server; it is never sent to {origin.Host}, which is not a TEST Store on this computer. Sign in with HCG (production) for this Store."
            : $"Your HCG sign-in ({connect.Environment}) is never sent to {origin.Host}, a Store on this computer (a TEST server). Use a TEST sign-in for a TEST Store.";
    }
}
