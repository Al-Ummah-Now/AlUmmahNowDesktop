using System.Net;
using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge.Passkeys;
using Xunit;

namespace AlUmmahNow.Platform.Windows.Tests;

public class PasskeyFlowTests
{
    private const string ClientId = "29b4792a-caad-4814-a83f-4337bc6a1aeb";
    private const string Authorize = "https://auth.qa.alummahnow.net/authorize?response_type=code&client_id=" + ClientId
        + "&redirect_uri=http%3A%2F%2F127.0.0.1%2Fauth%2Fcallback&state=st%2B1&code_challenge=CHAL-lenge_x&code_challenge_method=S256&scope=openid+profile";

    private readonly FakeIssuer _issuer = new();
    private readonly FakeWebAuthn _webAuthn = new();

    private PasskeyFlows Flows() => new(new NativePasskeyClient(new HttpClient(_issuer), new Uri("https://auth.qa.alummahnow.net/"), "Test/1"), _webAuthn);

    private void SignInServer(string code = "the-code")
    {
        _issuer.On("/native/passkey/options", FakeIssuer.Json(new { challenge_id = "ch1", publicKey = Server.RequestOptions() }));
        _issuer.On("/native/passkey/verify", FakeIssuer.Json(new { code }));
    }

    private void RegisterServer()
    {
        _issuer.On("/native/passkey/register/options", FakeIssuer.Json(new { challenge_id = "rc1", publicKey = Server.CreationOptions() }));
        _issuer.On("/native/passkey/register/verify", FakeIssuer.Json(new { id = "cred-1", name = "Windows Hello", created_at = 1763000000000 }));
    }

    private static Func<HttpResponseMessage> StepUp() => FakeIssuer.Json(new { error = "step_up_required", error_description = "Sign in again" }, HttpStatusCode.Unauthorized);

    [Fact]
    public void Authorize_url_parameters_are_read()
    {
        var p = AuthorizeParameters.Parse(Authorize);
        Assert.Equal(ClientId, p.ClientId);
        Assert.Equal("http://127.0.0.1/auth/callback", p.RedirectUri);
        Assert.Equal("st+1", p.State);
        Assert.Equal("CHAL-lenge_x", p.CodeChallenge);
        Assert.Equal("http://127.0.0.1/auth/callback?code=a%2Fb&state=st%2B1", p.CallbackWith("a/b"));
    }

    [Fact]
    public void Authorize_url_without_pkce_is_refused()
    {
        Assert.Throws<PasskeyException>(() => AuthorizeParameters.Parse("https://a/authorize?client_id=c&redirect_uri=r&state=s"));
        Assert.Throws<PasskeyException>(() => AuthorizeParameters.Parse("https://a/authorize?client_id=c&redirect_uri=r&state=s&code_challenge=x&code_challenge_method=plain"));
    }

    [Fact]
    public async Task Sign_in_turns_a_passkey_into_the_pkce_callback()
    {
        SignInServer();
        var callback = await Flows().SignInAsync(Authorize);
        Assert.Equal("http://127.0.0.1/auth/callback?code=the-code&state=st%2B1", callback);

        var options = Assert.Single(_issuer.To("/native/passkey/options"));
        Assert.Null(options.Bearer);
        Assert.Equal(ClientId, options.Body.GetProperty("client_id").GetString());
        Assert.Equal("CHAL-lenge_x", options.Body.GetProperty("code_challenge").GetString());
        Assert.Equal("S256", options.Body.GetProperty("code_challenge_method").GetString());

        // The platform got the server's RP id and challenge.
        Assert.Equal("qa.alummahnow.net", _webAuthn.LastRequest!.RpId);
        Assert.Equal(Server.Challenge, _webAuthn.LastRequest.Challenge);

        var verify = Assert.Single(_issuer.To("/native/passkey/verify"));
        Assert.Equal("ch1", verify.Body.GetProperty("challenge_id").GetString());
        Assert.Equal(ClientId, verify.Body.GetProperty("client_id").GetString());
        var credential = verify.Body.GetProperty("credential");
        Assert.Equal("qrs", credential.GetProperty("id").GetString());
        var clientData = JsonDocument.Parse(Base64Url.Decode(credential.GetProperty("response").GetProperty("clientDataJSON").GetString()!)).RootElement;
        Assert.Equal("https://qa.alummahnow.net", clientData.GetProperty("origin").GetString());
        Assert.Equal("webauthn.get", clientData.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Sign_in_stops_when_the_sheet_is_closed()
    {
        SignInServer();
        _webAuthn.Fail = new PasskeyException(PasskeyError.Cancelled, "closed");
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.Cancelled, ex.Error);
        Assert.Empty(_issuer.To("/native/passkey/verify"));
        Assert.False(PasskeyOutcome.ForError(ex, signIn: true).ShowsDialog);
    }

    [Fact]
    public async Task Sign_in_without_the_platform_api_is_unavailable()
    {
        _webAuthn.IsAvailable = false;
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.Unavailable, ex.Error);
        Assert.Empty(_issuer.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_credential", PasskeyError.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, "unknown_credential", PasskeyError.Rejected)]
    [InlineData(HttpStatusCode.NotFound, "challenge_expired", PasskeyError.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", PasskeyError.Failed)]
    [InlineData(HttpStatusCode.BadGateway, null, PasskeyError.Failed)]
    public async Task Verify_errors_map(HttpStatusCode status, string? error, PasskeyError expected)
    {
        _issuer.On("/native/passkey/options", FakeIssuer.Json(new { challenge_id = "ch1", publicKey = Server.RequestOptions() }));
        _issuer.On("/native/passkey/verify", error == null
            ? () => new HttpResponseMessage(status) { Content = new StringContent("<html>bad gateway</html>") }
            : FakeIssuer.Json(new { error, error_description = "x" }, status));
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(expected, ex.Error);
        Assert.Equal(error, ex.ServerError);
    }

    [Theory]
    [InlineData("https://auth.qa.alummahnow.net", "qa.alummahnow.net", true)]
    [InlineData("https://auth.qa.alummahnow.net", "alummahnow.net", true)]
    [InlineData("https://auth.alummahnow.net", "alummahnow.net", true)]
    [InlineData("https://auth.alummahnow.net", "ALUMMAHNOW.NET.", true)]
    [InlineData("http://localhost:3301", "localhost", true)]
    [InlineData("https://auth.qa.alummahnow.net", "net", false)]
    [InlineData("https://auth.qa.alummahnow.net", "evilalummahnow.net", false)]
    [InlineData("https://auth.qa.alummahnow.net", "google.com", false)]
    [InlineData("https://auth.qa.alummahnow.net", "stage.alummahnow.net", false)]
    [InlineData("https://auth.qa.alummahnow.net", "", false)]
    public void Rp_id_is_pinned_to_the_issuer(string issuer, string rpId, bool allowed) =>
        Assert.Equal(allowed, NativePasskeyClient.IsAllowedRpId(new Uri(issuer), rpId));

    [Fact]
    public async Task Options_for_another_site_are_refused_before_the_sheet_opens()
    {
        _issuer.On("/native/passkey/options", FakeIssuer.Json(new { challenge_id = "ch1", publicKey = Server.RequestOptions("google.com") }));
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.Failed, ex.Error);
        Assert.Null(_webAuthn.LastRequest);
    }

    [Fact]
    public async Task Rate_limit_carries_retry_after()
    {
        _issuer.On("/native/passkey/options", FakeIssuer.Json(new { error = "rate_limited" }, (HttpStatusCode)429, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42))));
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.RateLimited, ex.Error);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
        Assert.Equal("passkey.error.rateLimited", PasskeyOutcome.ForError(ex, signIn: true).MessageKey);
    }

    [Fact]
    public async Task No_connection_is_offline()
    {
        _issuer.Throw = new HttpRequestException("no route");
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.Offline, ex.Error);
    }

    [Fact]
    public async Task Malformed_options_fail_cleanly()
    {
        _issuer.On("/native/passkey/options", FakeIssuer.Json(new { challenge_id = "ch1" }));
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().SignInAsync(Authorize));
        Assert.Equal(PasskeyError.Failed, ex.Error);
    }

    [Fact]
    public async Task Add_registers_with_the_bearer_token()
    {
        RegisterServer();
        var saved = await Flows().AddAsync(_ => Task.FromResult<string?>("tok"), _ => throw new InvalidOperationException("no step-up expected"));
        Assert.Equal("cred-1", saved.Id);
        Assert.Equal("Windows Hello", saved.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1763000000000), saved.CreatedAt);
        Assert.All(_issuer.Requests, r => Assert.Equal("tok", r.Bearer));
        Assert.Equal("qa.alummahnow.net", _webAuthn.LastCreation!.RpId);
        var verify = Assert.Single(_issuer.To("/native/passkey/register/verify"));
        Assert.Equal("rc1", verify.Body.GetProperty("challenge_id").GetString());
        Assert.Equal("zA", verify.Body.GetProperty("credential").GetProperty("id").GetString());
        Assert.False(verify.Body.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Add_signs_in_again_once_on_step_up_then_retries()
    {
        _issuer.On("/native/passkey/register/options", StepUp());
        RegisterServer();
        var tokens = new Queue<string>(new[] { "old", "fresh" });
        var reSignIns = 0;
        var saved = await Flows().AddAsync(_ => Task.FromResult<string?>(tokens.Dequeue()), _ =>
        {
            reSignIns++;
            return Task.FromResult(true);
        });
        Assert.Equal("cred-1", saved.Id);
        Assert.Equal(1, reSignIns);
        Assert.Equal(new[] { "old", "fresh" }, _issuer.To("/native/passkey/register/options").Select(r => r.Bearer));
        Assert.Equal("fresh", Assert.Single(_issuer.To("/native/passkey/register/verify")).Bearer);
    }

    [Fact]
    public async Task Add_step_up_on_verify_also_retries_with_a_new_challenge()
    {
        _issuer.On("/native/passkey/register/options", FakeIssuer.Json(new { challenge_id = "stale", publicKey = Server.CreationOptions() }));
        _issuer.On("/native/passkey/register/verify", StepUp());
        RegisterServer();
        var saved = await Flows().AddAsync(_ => Task.FromResult<string?>("t"), _ => Task.FromResult(true));
        Assert.Equal("cred-1", saved.Id);
        Assert.Equal(new[] { "stale", "rc1" }, _issuer.To("/native/passkey/register/verify").Select(r => r.Body.GetProperty("challenge_id").GetString()));
    }

    [Fact]
    public async Task Add_does_not_loop_on_repeated_step_up()
    {
        _issuer.On("/native/passkey/register/options", StepUp(), StepUp(), StepUp());
        var reSignIns = 0;
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().AddAsync(_ => Task.FromResult<string?>("t"), _ =>
        {
            reSignIns++;
            return Task.FromResult(true);
        }));
        Assert.Equal(PasskeyError.StepUpRequired, ex.Error);
        Assert.Equal(1, reSignIns);
        Assert.Equal(2, _issuer.To("/native/passkey/register/options").Count);
    }

    [Fact]
    public async Task Add_cancelled_re_sign_in_is_silent()
    {
        _issuer.On("/native/passkey/register/options", StepUp());
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().AddAsync(_ => Task.FromResult<string?>("t"), _ => Task.FromResult(false)));
        Assert.Equal(PasskeyError.Cancelled, ex.Error);
        Assert.False(PasskeyOutcome.ForError(ex, signIn: false).ShowsDialog);
    }

    [Fact]
    public async Task Add_when_signed_out_asks_for_a_sign_in_first()
    {
        RegisterServer();
        var tokens = new Queue<string?>(new[] { null, "t" });
        var reSignIns = 0;
        var saved = await Flows().AddAsync(_ => Task.FromResult(tokens.Dequeue()), _ =>
        {
            reSignIns++;
            return Task.FromResult(true);
        });
        Assert.Equal(1, reSignIns);
        Assert.Equal("cred-1", saved.Id);
    }

    [Theory]
    [InlineData("passkey_limit", PasskeyError.Limit, "passkey.error.limit")]
    [InlineData("credential_exists", PasskeyError.AlreadyRegistered, "passkey.error.exists")]
    [InlineData("invalid_registration", PasskeyError.Rejected, "passkey.error.addFailed")]
    public async Task Add_errors_map(string error, PasskeyError expected, string key)
    {
        _issuer.On("/native/passkey/register/options", FakeIssuer.Json(new { challenge_id = "rc1", publicKey = Server.CreationOptions() }));
        _issuer.On("/native/passkey/register/verify", FakeIssuer.Json(new { error }, HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<PasskeyException>(() => Flows().AddAsync(_ => Task.FromResult<string?>("t"), _ => Task.FromResult(true)));
        Assert.Equal(expected, ex.Error);
        var outcome = PasskeyOutcome.ForError(ex, signIn: false);
        Assert.Equal(key, outcome.MessageKey);
        Assert.False(outcome.OfferOtherWay);
    }

    [Theory]
    [InlineData(PasskeyError.Cancelled, null, false)]
    [InlineData(PasskeyError.NoPasskey, "passkey.error.none", true)]
    [InlineData(PasskeyError.Rejected, "passkey.error.rejected", true)]
    [InlineData(PasskeyError.Unavailable, "passkey.error.unsupported", true)]
    [InlineData(PasskeyError.Offline, "passkey.error.offline", false)]
    [InlineData(PasskeyError.Failed, "passkey.error.generic", true)]
    public void Sign_in_outcomes(PasskeyError error, string? key, bool otherWay)
    {
        var o = PasskeyOutcome.ForError(new PasskeyException(error, "x"), signIn: true);
        Assert.Equal(key, o.MessageKey);
        Assert.Equal(otherWay, o.OfferOtherWay);
    }

    [Fact]
    public void Cancelled_operations_are_silent()
    {
        Assert.False(PasskeyOutcome.ForError(new OperationCanceledException(), signIn: true).ShowsDialog);
    }

    [Fact]
    public void Every_outcome_key_has_copy_in_both_languages()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AlUmmahNow.sln"))) root = Path.GetDirectoryName(root)!;
        foreach (var lang in new[] { "en", "ar" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "AlUmmahNow.App", "I18n", lang + ".json"), Encoding.UTF8));
            foreach (var error in Enum.GetValues<PasskeyError>())
            {
                foreach (var signIn in new[] { true, false })
                {
                    if (PasskeyOutcome.ForError(new PasskeyException(error, "x"), signIn).MessageKey is { } key) Assert.True(doc.RootElement.TryGetProperty(key, out _), lang + " is missing " + key);
                }
            }
        }
    }
}
