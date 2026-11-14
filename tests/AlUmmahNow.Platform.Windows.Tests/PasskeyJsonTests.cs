using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge.Passkeys;
using Xunit;

namespace AlUmmahNow.Platform.Windows.Tests;

public class PasskeyJsonTests
{
    private static JsonElement Parse(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    [Theory]
    [InlineData(new byte[0], "")]
    [InlineData(new byte[] { 0xFB }, "-w")]
    [InlineData(new byte[] { 0xFF, 0xFE }, "__4")]
    [InlineData(new byte[] { 1, 2, 3 }, "AQID")]
    public void Base64url_is_unpadded_and_url_safe(byte[] bytes, string expected)
    {
        Assert.Equal(expected, Base64Url.Encode(bytes));
        Assert.Equal(bytes, Base64Url.Decode(expected));
    }

    [Fact]
    public void Base64url_accepts_padding_and_rejects_garbage()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFE }, Base64Url.Decode("__4="));
        Assert.Throws<FormatException>(() => Base64Url.Decode("A"));
    }

    [Fact]
    public void Request_options_decode()
    {
        var r = WebAuthnJson.ParseRequestOptions(Parse(new
        {
            challenge = Base64Url.Encode(Server.Challenge),
            timeout = 60000,
            rpId = "alummahnow.net",
            userVerification = "required",
            allowCredentials = new object[] { new { id = "AQID", type = "public-key", transports = new[] { "usb", "hybrid" } } },
        }));
        Assert.Equal(Server.Challenge, r.Challenge);
        Assert.Equal("alummahnow.net", r.RpId);
        Assert.Equal(60000, r.TimeoutMs);
        Assert.Equal("required", r.UserVerification);
        var c = Assert.Single(r.AllowCredentials);
        Assert.Equal(new byte[] { 1, 2, 3 }, c.Id);
        Assert.Equal(new[] { "usb", "hybrid" }, c.Transports);
    }

    [Fact]
    public void Request_options_defaults_for_discoverable_sign_in()
    {
        var r = WebAuthnJson.ParseRequestOptions(Parse(new { challenge = "AQID", rpId = "qa.alummahnow.net" }));
        Assert.Empty(r.AllowCredentials);
        Assert.Equal(WebAuthnJson.DefaultTimeoutMs, r.TimeoutMs);
        Assert.Equal("preferred", r.UserVerification);
    }

    [Fact]
    public void Request_options_without_rp_id_fail()
    {
        var ex = Assert.Throws<PasskeyException>(() => WebAuthnJson.ParseRequestOptions(Parse(new { challenge = "AQID" })));
        Assert.Equal(PasskeyError.Failed, ex.Error);
    }

    [Fact]
    public void Creation_options_decode()
    {
        var c = WebAuthnJson.ParseCreationOptions(Parse(Server.CreationOptions()));
        Assert.Equal("qa.alummahnow.net", c.RpId);
        Assert.Equal("Al Ummah Now QA", c.RpName);
        Assert.Equal(new byte[] { 9, 8, 7 }, c.UserId);
        Assert.Equal("amina@example.com", c.UserName);
        Assert.Equal("Amina", c.UserDisplayName);
        Assert.Equal(new[] { -7, -257 }, c.Algorithms);
        Assert.Equal("required", c.ResidentKey);
        Assert.Equal("required", c.UserVerification);
        Assert.Null(c.AuthenticatorAttachment);
        Assert.Equal("none", c.Attestation);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(c.ExcludeCredentials).Id);
    }

    [Fact]
    public void Creation_options_need_algorithms()
    {
        var ex = Assert.Throws<PasskeyException>(() => WebAuthnJson.ParseCreationOptions(Parse(new
        {
            challenge = "AQID",
            rp = new { id = "x" },
            user = new { id = "AQ", name = "n" },
            pubKeyCredParams = Array.Empty<object>(),
        })));
        Assert.Contains("pubKeyCredParams", ex.Message);
    }

    [Fact]
    public void Client_data_claims_the_rp_origin()
    {
        var json = Encoding.UTF8.GetString(WebAuthnJson.ClientData("webauthn.get", Server.Challenge, "qa.alummahnow.net"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("webauthn.get", root.GetProperty("type").GetString());
        Assert.Equal(Base64Url.Encode(Server.Challenge), root.GetProperty("challenge").GetString());
        Assert.Equal("https://qa.alummahnow.net", root.GetProperty("origin").GetString());
        Assert.False(root.GetProperty("crossOrigin").GetBoolean());
        // Field order as browsers write it: type, challenge, origin.
        Assert.StartsWith("{\"type\":\"webauthn.get\",\"challenge\":", json);
    }

    [Fact]
    public void Authentication_response_is_L3_json()
    {
        var a = new PasskeyAssertion(new byte[] { 0xFB, 0xFF }, Encoding.UTF8.GetBytes("{}"), new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, "cross-platform");
        using var doc = JsonDocument.Parse(WebAuthnJson.AuthenticationResponse(a));
        var r = doc.RootElement;
        Assert.Equal("-_8", r.GetProperty("id").GetString());
        Assert.Equal("-_8", r.GetProperty("rawId").GetString());
        Assert.Equal("public-key", r.GetProperty("type").GetString());
        Assert.Equal("cross-platform", r.GetProperty("authenticatorAttachment").GetString());
        var resp = r.GetProperty("response");
        Assert.Equal("e30", resp.GetProperty("clientDataJSON").GetString());
        Assert.Equal("AQ", resp.GetProperty("authenticatorData").GetString());
        Assert.Equal("Ag", resp.GetProperty("signature").GetString());
        Assert.Equal("Aw", resp.GetProperty("userHandle").GetString());
        Assert.Equal(JsonValueKind.Object, r.GetProperty("clientExtensionResults").ValueKind);
    }

    [Fact]
    public void Authentication_response_leaves_out_empty_user_handle_and_unknown_attachment()
    {
        var a = new PasskeyAssertion(new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }, new byte[] { 4 }, null, null);
        using var doc = JsonDocument.Parse(WebAuthnJson.AuthenticationResponse(a));
        Assert.False(doc.RootElement.GetProperty("response").TryGetProperty("userHandle", out _));
        Assert.False(doc.RootElement.TryGetProperty("authenticatorAttachment", out _));
    }

    [Fact]
    public void Registration_response_is_L3_json()
    {
        var a = new PasskeyAttestation(new byte[] { 1, 2, 3 }, new byte[] { 0x7B, 0x7D }, new byte[] { 0xA3 }, new byte[] { 5 }, new[] { "hybrid", "internal" }, "platform");
        using var doc = JsonDocument.Parse(WebAuthnJson.RegistrationResponse(a));
        var r = doc.RootElement;
        Assert.Equal("AQID", r.GetProperty("id").GetString());
        Assert.Equal("AQID", r.GetProperty("rawId").GetString());
        Assert.Equal("public-key", r.GetProperty("type").GetString());
        var resp = r.GetProperty("response");
        Assert.Equal("e30", resp.GetProperty("clientDataJSON").GetString());
        Assert.Equal("ow", resp.GetProperty("attestationObject").GetString());
        Assert.Equal("BQ", resp.GetProperty("authenticatorData").GetString());
        Assert.Equal(new[] { "hybrid", "internal" }, resp.GetProperty("transports").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("platform", r.GetProperty("authenticatorAttachment").GetString());
    }
}
