using System.Net;
using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge.Passkeys;

namespace AlUmmahNow.Platform.Windows.Tests;

/// <summary>A fake issuer: answers by path, records each request (path, auth header, JSON body).</summary>
public sealed class FakeIssuer : HttpMessageHandler
{
    public readonly List<(string Path, string? Bearer, JsonElement Body)> Requests = new();
    public readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> Answers = new();
    public Exception? Throw;

    public void On(string path, params Func<HttpResponseMessage>[] answers)
    {
        if (!Answers.TryGetValue(path, out var q)) Answers[path] = q = new Queue<Func<HttpResponseMessage>>();
        foreach (var a in answers) q.Enqueue(a);
    }

    public static Func<HttpResponseMessage> Json(object body, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage>? tweak = null) => () =>
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        tweak?.Invoke(r);
        return r;
    };

    public List<(string Path, string? Bearer, JsonElement Body)> To(string path) => Requests.Where(r => r.Path == path).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Throw != null) throw Throw;
        var text = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter, JsonDocument.Parse(text).RootElement.Clone()));
        var path = request.RequestUri!.AbsolutePath;
        if (Answers.TryGetValue(path, out var q) && q.Count > 0) return q.Dequeue()();
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not_found\"}") };
    }
}

/// <summary>Options the issuer sends (shaped like @simplewebauthn/server output).</summary>
public static class Server
{
    public static readonly byte[] Challenge = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public static object RequestOptions(string rpId = "qa.alummahnow.net") => new
    {
        challenge = Base64Url.Encode(Challenge),
        timeout = 300000,
        rpId,
        allowCredentials = Array.Empty<object>(),
        userVerification = "required",
    };

    public static object CreationOptions() => new
    {
        challenge = Base64Url.Encode(Challenge),
        rp = new { name = "Al Ummah Now QA", id = "qa.alummahnow.net" },
        user = new { id = Base64Url.Encode(new byte[] { 9, 8, 7 }), name = "amina@example.com", displayName = "Amina" },
        pubKeyCredParams = new object[] { new { alg = -7, type = "public-key" }, new { alg = -257, type = "public-key" } },
        timeout = 300000,
        attestation = "none",
        excludeCredentials = new object[] { new { id = Base64Url.Encode(new byte[] { 1, 2, 3 }), type = "public-key", transports = new[] { "internal", "hybrid" } } },
        authenticatorSelection = new { residentKey = "required", userVerification = "required", requireResidentKey = true },
    };
}

/// <summary>The platform API seam, scripted.</summary>
public sealed class FakeWebAuthn : IWebAuthn
{
    public bool IsAvailable { get; set; } = true;
    public PasskeyRequest? LastRequest;
    public PasskeyCreation? LastCreation;
    public Exception? Fail;

    public Task<PasskeyAssertion> GetAssertionAsync(PasskeyRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        if (Fail != null) throw Fail;
        var clientData = WebAuthnJson.ClientData("webauthn.get", request.Challenge, request.RpId);
        return Task.FromResult(new PasskeyAssertion(new byte[] { 0xAA, 0xBB }, clientData, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6 }, "platform"));
    }

    public Task<PasskeyAttestation> MakeCredentialAsync(PasskeyCreation creation, CancellationToken cancellationToken = default)
    {
        LastCreation = creation;
        if (Fail != null) throw Fail;
        var clientData = WebAuthnJson.ClientData("webauthn.create", creation.Challenge, creation.RpId);
        return Task.FromResult(new PasskeyAttestation(new byte[] { 0xCC }, clientData, new byte[] { 0xA0 }, new byte[] { 7 }, new[] { "hybrid", "internal" }, "platform"));
    }
}
