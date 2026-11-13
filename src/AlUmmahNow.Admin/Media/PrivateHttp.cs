using System.Net;
using System.Net.Http.Headers;

namespace AlUmmahNow.Admin.Media;

/// <summary>
/// The one HTTP stack for the Admin app (owner: "never cache or download on phone or native app
/// ever, only encrypted stream"). .NET's HttpClient has no response cache; this also turns off
/// cookies, credentials and redirects to other schemes, asks every hop for <c>no-store</c>, and
/// refuses anything but TLS (loopback is allowed only for tests and the local stack).
/// Bodies are read into memory only. Nothing here writes a file.
/// </summary>
public static class PrivateHttp
{
    public static HttpClient Create(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = true,
            Credentials = null,
            PreAuthenticate = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        return client;
    }

    /// <summary>TLS only; plain http only to loopback (tests, local stack).</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>What a log line may say about a URL: scheme and host only (never the path, query or signature).</summary>
    public static string Describe(Uri? uri) => uri == null || !uri.IsAbsoluteUri ? "(none)" : uri.Scheme + "://" + uri.Host;
}
