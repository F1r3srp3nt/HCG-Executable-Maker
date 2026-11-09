using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HCG.ExecutableMaker.Account;

/// <summary>
/// RFC 8252 loopback redirect receiver: listens on 127.0.0.1 only, on the registered port, for exactly one request to
/// the registered path, and answers with an HCG-branded page telling the user to return to the app. It accepts no
/// other host, never echoes the query and stops after the first valid callback (or on cancellation/timeout).
/// </summary>
public sealed class LoopbackCallbackListener : IDisposable
{
    private readonly TcpListener _listener;
    public Uri RedirectUri { get; }

    private LoopbackCallbackListener(TcpListener listener, Uri redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    /// <summary>Starts on the first registered redirect URI whose port is free; null when every port is in use.</summary>
    public static LoopbackCallbackListener? StartFirstAvailable(IEnumerable<Uri> registered)
    {
        foreach (var uri in registered)
        {
            var listener = new TcpListener(IPAddress.Loopback, uri.Port);
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start(4);
                return new LoopbackCallbackListener(listener, uri);
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        return null;
    }

    /// <summary>Waits for the browser's redirect and returns its query parameters (code/state or error).</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            client.ReceiveTimeout = 10_000;
            await using var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
            var parts = requestLine?.Split(' ');
            if (parts is not { Length: 3 } || parts[0] != "GET")
            {
                await RespondAsync(stream, 400, "Bad request", "This address only receives the HCG sign-in response.", ct).ConfigureAwait(false);
                continue;
            }
            var target = parts[1];
            var q = target.IndexOf('?');
            var path = q < 0 ? target : target[..q];
            if (path != RedirectUri.AbsolutePath)
            {
                await RespondAsync(stream, 404, "Not found", "This address only receives the HCG sign-in response.", ct).ConfigureAwait(false);
                continue;
            }
            var query = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
            var ok = query.ContainsKey("code");
            await RespondAsync(stream, 200,
                ok ? "Signed in to HCG" : "Sign-in not completed",
                ok ? "You can close this tab and return to HCG Executable Maker." : "Return to HCG Executable Maker to see what happened.", ct).ConfigureAwait(false);
            return query;
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value); // a repeated parameter keeps its first value
        }
        return result;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string message, CancellationToken ct)
    {
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}} · HCG</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0a1220;color:#e6eef8;font:16px system-ui,-apple-system,Segoe UI,sans-serif}
            main{max-width:420px;padding:32px;border:1px solid #1d3550;border-radius:14px;background:#0f1b2d;text-align:center}
            .k{letter-spacing:.3em;font-size:11px;color:#5fd3f3}h1{font-size:22px;margin:12px 0}p{color:#9fb3c8}</style></head>
            <body><main><div class="k">HAKMI CAPITAL GROUP</div><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></main></body></html>
            """;
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
