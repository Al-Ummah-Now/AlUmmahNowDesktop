using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>The real core AuthManager against a local OAuth server, with a "browser" that follows the redirect.</summary>
public sealed class CoreStudioAccountsTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly string _issuer;
    private readonly ConcurrentQueue<string> _revoked = new();
    private readonly ConcurrentQueue<string> _authorizeQueries = new();
    private string _nextUser = "u1";

    public CoreStudioAccountsTests()
    {
        var port = FreePort();
        _issuer = "http://127.0.0.1:" + port;
        _server.Prefixes.Add(_issuer + "/");
        _server.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose() => _server.Close();

    private sealed class MemoryStore : ISecureStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _items = new();
        public byte[]? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, byte[] value) => _items[key] = value;
        public void Delete(string key) => _items.TryRemove(key, out _);
        public string[] List(string prefix) => _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Plays the browser: the auth server "signs in" at once and redirects to the loopback.</summary>
    private sealed class FollowingBrowser : IBrowserLauncher
    {
        private readonly CoreStudioAccountsTests _owner;
        public FollowingBrowser(CoreStudioAccountsTests owner) => _owner = owner;

        public void Open(Uri uri)
        {
            var q = HttpUtility.ParseQueryString(uri.Query);
            _owner._authorizeQueries.Enqueue(uri.Query);
            var redirect = q["redirect_uri"] + "?code=code-" + _owner._nextUser + "&state=" + Uri.EscapeDataString(q["state"]!);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync(redirect);
            });
        }
    }

    [Fact]
    public async Task SignsInOnALoopbackRedirectAddsSwitchesAndRevokesOnSignOut()
    {
        using var accounts = new CoreStudioAccounts(new Uri(_issuer), "studio-desktop", new MemoryStore(), new FollowingBrowser(this), "done", TimeSpan.FromSeconds(20));
        var changed = 0;
        accounts.Changed += (_, _) => Interlocked.Increment(ref changed);

        var first = await accounts.SignInAsync(SignInMode.Default);
        Assert.Equal("u1", first.UserId);
        Assert.Equal("u1@example.com", first.Email);
        Assert.True(_authorizeQueries.TryDequeue(out var q1));
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A", q1);
        Assert.DoesNotContain("prompt=", q1);

        _nextUser = "u2";
        await accounts.SignInAsync(SignInMode.AddAccount);
        Assert.True(_authorizeQueries.TryDequeue(out var q2));
        Assert.Contains("prompt=login", q2);
        Assert.Equal(2, accounts.Accounts.Count);
        Assert.Equal("u2", accounts.Active!.UserId);

        accounts.Switch("u1");
        Assert.Equal("u1", accounts.Active!.UserId);
        accounts.SetActiveChannel("c9");
        Assert.Equal("c9", accounts.Active!.ActiveChannelId);

        await accounts.SignOutAsync("u1");
        Assert.Equal("u2", Assert.Single(accounts.Accounts).UserId);
        Assert.Equal("u2", accounts.Active!.UserId);
        Assert.Contains("token=rt-u1", _revoked);
        Assert.True(changed >= 4);

        // The API transport follows the active account and asks the core for its token.
        var transport = new CoreApiTransport(accounts, new Uri(_issuer), "test", "en");
        var body = await transport.SendAsync(new ApiRequest("get_ChannelAccessList"));
        Assert.Contains("\"c1\"", body);
    }

    [Fact]
    public async Task ATimedOutSignInFailsCleanly()
    {
        using var accounts = new CoreStudioAccounts(new Uri(_issuer), "studio-desktop", new MemoryStore(), new NoBrowser(), "done", TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<AuthFailedException>(() => accounts.SignInAsync(SignInMode.Default));
        Assert.Empty(accounts.Accounts);
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _server.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            var form = ctx.Request.HasEntityBody ? new StreamReader(ctx.Request.InputStream).ReadToEnd() : "";
            string json = "{}";
            if (path == "/token")
            {
                var f = HttpUtility.ParseQueryString(form);
                var user = (f["code"] ?? "code-u1").Substring(5);
                if (f["grant_type"] == "authorization_code" && (f["redirect_uri"] ?? "").StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && !string.IsNullOrEmpty(f["code_verifier"]))
                {
                    json = "{\"access_token\":\"" + Jwt(user) + "\",\"refresh_token\":\"rt-" + user + "\",\"expires_in\":3600,\"token_type\":\"bearer\"}";
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    json = "{\"error\":\"invalid_grant\"}";
                }
            }
            else if (path == "/token/revoke")
            {
                _revoked.Enqueue(form.Split('&').First(p => p.StartsWith("token=", StringComparison.Ordinal)));
            }
            else if (path == "/api/channels/access")
            {
                json = ctx.Request.Headers["Authorization"]?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                    ? "{\"success\":true,\"data\":[{\"id\":\"c1\",\"userId\":\"u2\",\"name\":\"C\",\"isMain\":true,\"role\":\"owner\",\"capabilities\":[]}]}"
                    : "{}";
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
    }

    private static string Jwt(string user)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"ES256\"}") + "." + B64("{\"sub\":\"user:" + user + "\",\"properties\":{\"id\":\"" + user + "\",\"email\":\"" + user + "@example.com\"}}") + ".sig";
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
