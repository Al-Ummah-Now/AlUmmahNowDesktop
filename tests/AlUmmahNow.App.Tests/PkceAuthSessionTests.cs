using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using AlUmmahNow.App.ViewModels;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Temporary;
using Xunit;

namespace AlUmmahNow.App.Tests;

public class PkceAuthSessionTests
{
    private sealed class MemoryStore : ISecureStore
    {
        public readonly Dictionary<string, byte[]> Items = new();
        public bool Broken;
        public byte[]? Get(string key) => Items.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, byte[] value) { if (Broken) throw new IOException("no keyring"); Items[key] = value; }
        public void Delete(string key) => Items.Remove(key);
        public string[] List(string prefix) => Items.Keys.ToArray();
    }

    /// <summary>Plays the browser: requests the redirect the auth server would send.</summary>
    private sealed class FakeBrowser : IBrowserLauncher
    {
        public Func<Dictionary<string, string>, string>? Respond;
        public Uri? Opened;
        public Dictionary<string, string> Query = new();

        public void Open(Uri uri)
        {
            Opened = uri;
            var q = HttpUtility.ParseQueryString(uri.Query);
            Query = q.AllKeys.ToDictionary(k => k!, k => q[k]!);
            var redirect = Query["redirect_uri"];
            var callback = Respond!(Query);
            _ = Task.Run(async () =>
            {
                // A browser asks for the icon too, and that must not end the wait.
                await Get(new Uri(redirect).Port, "/favicon.ico");
                await Get(new Uri(redirect).Port, "/auth/callback?" + callback);
            });
        }

        private static async Task Get(int port, string path)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            await stream.WriteAsync(request);
            await new StreamReader(stream).ReadToEndAsync();
        }
    }

    private sealed class TokenServer : HttpMessageHandler
    {
        public readonly List<Dictionary<string, string>> Forms = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int ExpiresIn = 3600;
        public string Jwt = MakeJwt("""{"sub":"u1","properties":{"email":"a@example.org"}}""");
        public string NextRefresh = "r2";
        public int Delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var form = HttpUtility.ParseQueryString(await r.Content!.ReadAsStringAsync(ct));
            lock (Forms) Forms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            if (Delay > 0) await Task.Delay(Delay, ct);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent($$"""{"access_token":"{{Jwt}}","refresh_token":"{{NextRefresh}}","expires_in":{{ExpiresIn}}}"""),
            };
        }
    }

    private static string MakeJwt(string payload) =>
        "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";

    private static (PkceAuthSession Session, TokenServer Server, FakeBrowser Browser, MemoryStore Store) Make(TimeSpan? timeout = null)
    {
        var server = new TokenServer();
        var browser = new FakeBrowser { Respond = q => "code=abc&state=" + q["state"] };
        var store = new MemoryStore();
        return (new PkceAuthSession(new HttpClient(server), new Uri("https://auth.test"), "client-1", store, browser, "done", timeout), server, browser, store);
    }

    [Fact]
    public async Task Sign_in_uses_pkce_loopback_and_keeps_the_session()
    {
        var (s, server, browser, store) = Make();
        await s.SignInAsync();

        Assert.Equal("a@example.org", s.Account!.Email);
        Assert.Equal("S256", browser.Query["code_challenge_method"]);
        Assert.Equal("client-1", browser.Query["client_id"]);
        Assert.Contains("offline_access", browser.Query["scope"]);
        Assert.StartsWith("http://127.0.0.1:", browser.Query["redirect_uri"]);
        Assert.EndsWith("/auth/callback", browser.Query["redirect_uri"]);

        var form = Assert.Single(server.Forms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(browser.Query["redirect_uri"], form["redirect_uri"]);
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]));
        Assert.Equal(Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_'), browser.Query["code_challenge"]);
        Assert.NotEmpty(store.Items);
    }

    [Fact]
    public async Task Wrong_state_is_rejected_and_no_token_is_requested()
    {
        var (s, server, browser, _) = Make();
        browser.Respond = _ => "code=abc&state=forged";
        await Assert.ThrowsAsync<AuthFailedException>(() => s.SignInAsync());
        Assert.Empty(server.Forms);
        Assert.Null(s.Account);
    }

    [Fact]
    public async Task Server_error_is_reported()
    {
        var (s, _, browser, _) = Make();
        browser.Respond = q => "error=access_denied&state=" + q["state"];
        var ex = await Assert.ThrowsAsync<AuthFailedException>(() => s.SignInAsync());
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task Rejected_code_fails_sign_in()
    {
        var (s, server, _, _) = Make();
        server.Status = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<AuthFailedException>(() => s.SignInAsync());
    }

    [Fact]
    public async Task No_callback_times_out()
    {
        var (s, _, browser, _) = Make(TimeSpan.FromMilliseconds(100));
        var never = new NoCallback();
        var quiet = new PkceAuthSession(new HttpClient(new TokenServer()), new Uri("https://auth.test"), "c", new MemoryStore(), never, "done", TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<AuthFailedException>(() => quiet.SignInAsync());
    }

    private sealed class NoCallback : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    [Fact]
    public async Task Valid_token_is_reused_without_a_request()
    {
        var (s, server, _, _) = Make();
        await s.SignInAsync();
        await s.GetAccessTokenAsync();
        Assert.Single(server.Forms);
    }

    [Fact]
    public async Task Expired_token_refreshes_once_for_concurrent_callers_and_stores_rotation()
    {
        var (s, server, _, store) = Make();
        server.ExpiresIn = 1;
        await s.SignInAsync();
        server.ExpiresIn = 3600;
        server.Delay = 30;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.GetAccessTokenAsync()));

        var refreshes = server.Forms.Where(f => f["grant_type"] == "refresh_token").ToList();
        Assert.Single(refreshes);
        Assert.Equal("r2", refreshes[0]["refresh_token"]);
        Assert.Contains("r2", Encoding.UTF8.GetString(store.Items.Values.Single()));
    }

    [Fact]
    public async Task Rejected_refresh_signs_out()
    {
        var (s, server, _, store) = Make();
        server.ExpiresIn = 1;
        await s.SignInAsync();
        server.Status = HttpStatusCode.BadRequest;
        await Assert.ThrowsAsync<AuthRequiredException>(() => s.GetAccessTokenAsync());
        Assert.Null(s.Account);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Server_outage_during_refresh_keeps_the_session()
    {
        var (s, server, _, store) = Make();
        server.ExpiresIn = 1;
        await s.SignInAsync();
        server.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<CatalogException>(() => s.GetAccessTokenAsync());
        Assert.NotNull(s.Account);
        Assert.NotEmpty(store.Items);
    }

    [Fact]
    public async Task Restore_brings_back_the_account_and_sign_out_clears_it()
    {
        var (s, server, browser, store) = Make();
        await s.SignInAsync();
        var second = new PkceAuthSession(new HttpClient(server), new Uri("https://auth.test"), "client-1", store, browser, "done");
        second.Restore();
        Assert.Equal("u1", second.Account!.Subject);
        second.SignOut();
        Assert.Null(second.Account);
        Assert.Empty(store.Items);
        await Assert.ThrowsAsync<AuthRequiredException>(() => second.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Missing_keyring_still_signs_in_for_this_run()
    {
        var (s, _, _, store) = Make();
        store.Broken = true;
        await s.SignInAsync();
        Assert.NotNull(s.Account);
    }

    [Fact]
    public async Task View_model_tracks_account_and_failure()
    {
        var (s, _, browser, _) = Make();
        var vm = new AuthViewModel(s);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await vm.SignInAsync();
        Assert.True(vm.IsSignedIn);
        Assert.Equal("a@example.org", vm.AccountName);
        Assert.Contains(nameof(AuthViewModel.IsSignedIn), changes);

        vm.SignOut();
        Assert.False(vm.IsSignedIn);
        browser.Respond = _ => "code=x&state=bad";
        await vm.SignInAsync();
        Assert.True(vm.Failed);
        Assert.False(vm.IsBusy);
    }
}
