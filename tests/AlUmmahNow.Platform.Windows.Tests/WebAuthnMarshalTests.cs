using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.Platform.Windows.WebAuthn;
using Xunit;

namespace AlUmmahNow.Platform.Windows.Tests;

public class WebAuthnMarshalTests
{
    private static PasskeyRequest Request(params PasskeyDescriptor[] allow) =>
        new(Server.Challenge, "qa.alummahnow.net", 120000, "required", allow);

    private static PasskeyCreation Creation() =>
        WebAuthnJson.ParseCreationOptions(JsonDocument.Parse(JsonSerializer.Serialize(Server.CreationOptions())).RootElement);

    private static byte[] Bytes(IntPtr p, uint count)
    {
        var b = new byte[count];
        Marshal.Copy(p, b, 0, (int)count);
        return b;
    }

    [Theory]
    [InlineData(1u, 4u, 3u)]
    [InlineData(2u, 4u, 3u)]
    [InlineData(3u, 5u, 4u)]
    [InlineData(4u, 6u, 5u)]
    [InlineData(5u, 6u, 5u)]
    [InlineData(6u, 6u, 6u)]
    [InlineData(7u, 7u, 7u)]
    [InlineData(8u, 8u, 8u)]
    [InlineData(9u, 9u, 9u)]
    [InlineData(12u, 9u, 9u)]
    public void Option_versions_follow_the_api_version_table(uint api, uint assertion, uint makeCredential)
    {
        Assert.Equal(assertion, WebAuthnMarshal.GetAssertionOptionsVersion(api));
        Assert.Equal(makeCredential, WebAuthnMarshal.MakeCredentialOptionsVersion(api));
    }

    [Fact]
    public void Assertion_arguments_carry_rp_id_client_data_and_options()
    {
        using var arena = new NativeArena();
        var cancel = arena.Alloc(16);
        var call = WebAuthnMarshal.BuildAssertion(arena, Request(), 9, cancel);

        Assert.Equal("qa.alummahnow.net", Marshal.PtrToStringUni(call.RpId));
        var cd = Marshal.PtrToStructure<WEBAUTHN_CLIENT_DATA>(call.ClientData);
        Assert.Equal(1u, cd.dwVersion);
        Assert.Equal("SHA-256", Marshal.PtrToStringUni(cd.pwszHashAlgId));
        var json = Bytes(cd.pbClientDataJSON, cd.cbClientDataJSON);
        Assert.Equal(call.ClientDataJson, json);
        Assert.Contains("\"origin\":\"https://qa.alummahnow.net\"", Encoding.UTF8.GetString(json));
        Assert.Contains("\"type\":\"webauthn.get\"", Encoding.UTF8.GetString(json));

        var o = Marshal.PtrToStructure<WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS>(call.Options);
        Assert.Equal(9u, o.dwVersion);
        Assert.Equal(120000u, o.dwTimeoutMilliseconds);
        Assert.Equal(WebAuthnConst.USER_VERIFICATION_REQUIREMENT_REQUIRED, o.dwUserVerificationRequirement);
        Assert.Equal(WebAuthnConst.AUTHENTICATOR_ATTACHMENT_ANY, o.dwAuthenticatorAttachment);
        Assert.Equal(cancel, o.pCancellationId);
        // Discoverable: no allow list, so Windows offers Hello, keys, phones and plugins.
        Assert.Equal(IntPtr.Zero, o.pAllowCredentialList);
        Assert.Equal(0u, o.CredentialList.cCredentials);
        Assert.Equal(0, o.bAutoFill);
        Assert.Equal(IntPtr.Zero, o.pbPublicKeyCredentialRequestOptionsJSON);
    }

    [Fact]
    public void Assertion_allow_list_has_ids_and_transports()
    {
        using var arena = new NativeArena();
        var call = WebAuthnMarshal.BuildAssertion(arena, Request(new PasskeyDescriptor(new byte[] { 7, 7 }, new[] { "hybrid", "usb" })), 7, IntPtr.Zero);
        var o = Marshal.PtrToStructure<WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS>(call.Options);
        var list = Marshal.PtrToStructure<WEBAUTHN_CREDENTIAL_LIST>(o.pAllowCredentialList);
        Assert.Equal(1u, list.cCredentials);
        var ex = Marshal.PtrToStructure<WEBAUTHN_CREDENTIAL_EX>(Marshal.ReadIntPtr(list.ppCredentials));
        Assert.Equal(new byte[] { 7, 7 }, Bytes(ex.pbId, ex.cbId));
        Assert.Equal("public-key", Marshal.PtrToStringUni(ex.pwszCredentialType));
        Assert.Equal(WebAuthnConst.CTAP_TRANSPORT_HYBRID | WebAuthnConst.CTAP_TRANSPORT_USB, ex.dwTransports);
        // The v1 list is filled too, for APIs that read only it.
        var v1 = Marshal.PtrToStructure<WEBAUTHN_CREDENTIAL>(o.CredentialList.pCredentials);
        Assert.Equal(new byte[] { 7, 7 }, Bytes(v1.pbId, v1.cbId));
    }

    [Fact]
    public void Old_api_gets_no_cancellation_id()
    {
        using var arena = new NativeArena();
        var call = WebAuthnMarshal.BuildAssertion(arena, Request(), 1, new IntPtr(1234));
        var o = Marshal.PtrToStructure<WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS>(call.Options);
        Assert.Equal(4u, o.dwVersion);
        Assert.Equal(new IntPtr(1234), o.pCancellationId); // v3 field, inside v4.

        var make = WebAuthnMarshal.BuildMakeCredential(arena, Creation(), 1, new IntPtr(1234));
        var m = Marshal.PtrToStructure<WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS>(make.Options);
        Assert.Equal(3u, m.dwVersion);
        Assert.Equal(0, m.bPreferResidentKey);
    }

    [Fact]
    public void Make_credential_arguments_follow_the_server_options()
    {
        using var arena = new NativeArena();
        var call = WebAuthnMarshal.BuildMakeCredential(arena, Creation(), 9, IntPtr.Zero);

        var rp = Marshal.PtrToStructure<WEBAUTHN_RP_ENTITY_INFORMATION>(call.Rp);
        Assert.Equal("qa.alummahnow.net", Marshal.PtrToStringUni(rp.pwszId));
        Assert.Equal("Al Ummah Now QA", Marshal.PtrToStringUni(rp.pwszName));

        var user = Marshal.PtrToStructure<WEBAUTHN_USER_ENTITY_INFORMATION>(call.User);
        Assert.Equal(new byte[] { 9, 8, 7 }, Bytes(user.pbId, user.cbId));
        Assert.Equal("amina@example.com", Marshal.PtrToStringUni(user.pwszName));
        Assert.Equal("Amina", Marshal.PtrToStringUni(user.pwszDisplayName));

        var ps = Marshal.PtrToStructure<WEBAUTHN_COSE_CREDENTIAL_PARAMETERS>(call.Parameters);
        Assert.Equal(2u, ps.cCredentialParameters);
        var size = Marshal.SizeOf<WEBAUTHN_COSE_CREDENTIAL_PARAMETER>();
        var algs = Enumerable.Range(0, 2).Select(i => Marshal.PtrToStructure<WEBAUTHN_COSE_CREDENTIAL_PARAMETER>(ps.pCredentialParameters + (i * size))).ToList();
        Assert.Equal(new[] { -7, -257 }, algs.Select(a => a.lAlg));
        Assert.All(algs, a => Assert.Equal("public-key", Marshal.PtrToStringUni(a.pwszCredentialType)));

        var cd = Marshal.PtrToStructure<WEBAUTHN_CLIENT_DATA>(call.ClientData);
        Assert.Contains("\"type\":\"webauthn.create\"", Encoding.UTF8.GetString(Bytes(cd.pbClientDataJSON, cd.cbClientDataJSON)));

        var o = Marshal.PtrToStructure<WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS>(call.Options);
        Assert.Equal(9u, o.dwVersion);
        Assert.Equal(1, o.bRequireResidentKey);
        Assert.Equal(0, o.bPreferResidentKey);
        Assert.Equal(WebAuthnConst.USER_VERIFICATION_REQUIREMENT_REQUIRED, o.dwUserVerificationRequirement);
        Assert.Equal(WebAuthnConst.ATTESTATION_CONVEYANCE_PREFERENCE_NONE, o.dwAttestationConveyancePreference);
        Assert.Equal(WebAuthnConst.AUTHENTICATOR_ATTACHMENT_ANY, o.dwAuthenticatorAttachment);
        var exclude = Marshal.PtrToStructure<WEBAUTHN_CREDENTIAL_LIST>(o.pExcludeCredentialList);
        var ex = Marshal.PtrToStructure<WEBAUTHN_CREDENTIAL_EX>(Marshal.ReadIntPtr(exclude.ppCredentials));
        Assert.Equal(new byte[] { 1, 2, 3 }, Bytes(ex.pbId, ex.cbId));
        Assert.Equal(WebAuthnConst.CTAP_TRANSPORT_INTERNAL | WebAuthnConst.CTAP_TRANSPORT_HYBRID, ex.dwTransports);
    }

    [Fact]
    public void Assertion_is_read_from_native_memory()
    {
        using var arena = new NativeArena();
        var native = new WEBAUTHN_ASSERTION
        {
            dwVersion = 6,
            cbAuthenticatorData = 3,
            pbAuthenticatorData = arena.Bytes(new byte[] { 1, 2, 3 }),
            cbSignature = 2,
            pbSignature = arena.Bytes(new byte[] { 4, 5 }),
            Credential = new WEBAUTHN_CREDENTIAL { dwVersion = 1, cbId = 2, pbId = arena.Bytes(new byte[] { 0xAA, 0xBB }), pwszCredentialType = arena.WString("public-key") },
            cbUserId = 1,
            pbUserId = arena.Bytes(new byte[] { 9 }),
            dwUsedTransport = WebAuthnConst.CTAP_TRANSPORT_HYBRID,
        };
        var clientData = Encoding.UTF8.GetBytes("{\"type\":\"webauthn.get\"}");
        var a = WebAuthnMarshal.ReadAssertion(arena.Struct(native), clientData);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, a.CredentialId);
        Assert.Equal(new byte[] { 1, 2, 3 }, a.AuthenticatorData);
        Assert.Equal(new byte[] { 4, 5 }, a.Signature);
        Assert.Equal(new byte[] { 9 }, a.UserHandle);
        Assert.Same(clientData, a.ClientDataJson);
        Assert.Equal("cross-platform", a.AuthenticatorAttachment);
    }

    [Fact]
    public void Version_1_assertion_reads_only_version_1_fields()
    {
        using var arena = new NativeArena();
        // Only the version-1 prefix is allocated: reading a later field would be out of bounds.
        var v1Size = (int)Marshal.OffsetOf<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.Extensions));
        var p = arena.Alloc(v1Size);
        var full = new WEBAUTHN_ASSERTION
        {
            dwVersion = 1,
            cbAuthenticatorData = 1,
            pbAuthenticatorData = arena.Bytes(new byte[] { 1 }),
            cbSignature = 1,
            pbSignature = arena.Bytes(new byte[] { 2 }),
            Credential = new WEBAUTHN_CREDENTIAL { cbId = 1, pbId = arena.Bytes(new byte[] { 3 }) },
        };
        var tmp = arena.Struct(full);
        var copy = new byte[v1Size];
        Marshal.Copy(tmp, copy, 0, v1Size);
        Marshal.Copy(copy, 0, p, v1Size);
        var a = WebAuthnMarshal.ReadAssertion(p, Array.Empty<byte>());
        Assert.Null(a.AuthenticatorAttachment);
        Assert.Null(a.UserHandle);
    }

    [Fact]
    public void Assertion_without_a_signature_is_refused()
    {
        using var arena = new NativeArena();
        var p = arena.Struct(new WEBAUTHN_ASSERTION { dwVersion = 1, cbAuthenticatorData = 1, pbAuthenticatorData = arena.Bytes(new byte[] { 1 }) });
        Assert.Equal(PasskeyError.Failed, Assert.Throws<PasskeyException>(() => WebAuthnMarshal.ReadAssertion(p, Array.Empty<byte>())).Error);
    }

    [Fact]
    public void Attestation_is_read_from_native_memory()
    {
        using var arena = new NativeArena();
        var native = new WEBAUTHN_CREDENTIAL_ATTESTATION
        {
            dwVersion = 8,
            pwszFormatType = arena.WString("none"),
            cbAuthenticatorData = 1,
            pbAuthenticatorData = arena.Bytes(new byte[] { 5 }),
            cbAttestationObject = 2,
            pbAttestationObject = arena.Bytes(new byte[] { 0xA3, 0x01 }),
            cbCredentialId = 3,
            pbCredentialId = arena.Bytes(new byte[] { 1, 2, 3 }),
            dwUsedTransport = WebAuthnConst.CTAP_TRANSPORT_INTERNAL,
            dwTransports = WebAuthnConst.CTAP_TRANSPORT_INTERNAL | WebAuthnConst.CTAP_TRANSPORT_HYBRID,
        };
        var a = WebAuthnMarshal.ReadAttestation(arena.Struct(native), new byte[] { 1 });
        Assert.Equal(new byte[] { 1, 2, 3 }, a.CredentialId);
        Assert.Equal(new byte[] { 0xA3, 0x01 }, a.AttestationObject);
        Assert.Equal(new byte[] { 5 }, a.AuthenticatorData);
        Assert.Equal(new[] { "hybrid", "internal" }, a.Transports);
        Assert.Equal("platform", a.AuthenticatorAttachment);
    }

    [Fact]
    public void Older_attestation_uses_the_transport_it_was_made_on()
    {
        using var arena = new NativeArena();
        var native = new WEBAUTHN_CREDENTIAL_ATTESTATION
        {
            dwVersion = 3,
            cbAttestationObject = 1,
            pbAttestationObject = arena.Bytes(new byte[] { 0xA0 }),
            cbCredentialId = 1,
            pbCredentialId = arena.Bytes(new byte[] { 1 }),
            dwUsedTransport = WebAuthnConst.CTAP_TRANSPORT_USB,
            dwTransports = 0xFF, // Not in a v3 struct: must be ignored.
        };
        var a = WebAuthnMarshal.ReadAttestation(arena.Struct(native), Array.Empty<byte>());
        Assert.Equal(new[] { "usb" }, a.Transports);
        Assert.Equal("cross-platform", a.AuthenticatorAttachment);
    }

    [Theory]
    [InlineData(unchecked((int)0x80090036), true, PasskeyError.Cancelled)] // NTE_USER_CANCELLED
    [InlineData(unchecked((int)0x800704C7), true, PasskeyError.Cancelled)] // ERROR_CANCELLED
    [InlineData(unchecked((int)0x800705B4), false, PasskeyError.Cancelled)] // ERROR_TIMEOUT
    [InlineData(unchecked((int)0x80090011), true, PasskeyError.NoPasskey)] // NTE_NOT_FOUND
    [InlineData(unchecked((int)0x80090035), true, PasskeyError.NoPasskey)] // NTE_DEVICE_NOT_FOUND
    [InlineData(unchecked((int)0x80090011), false, PasskeyError.Failed)]
    [InlineData(unchecked((int)0x8009000F), false, PasskeyError.AlreadyRegistered)] // NTE_EXISTS
    [InlineData(unchecked((int)0x80070032), true, PasskeyError.Unavailable)] // ERROR_NOT_SUPPORTED
    [InlineData(unchecked((int)0x80090027), true, PasskeyError.Failed)] // NTE_INVALID_PARAMETER
    [InlineData(unchecked((int)0x80004005), true, PasskeyError.Failed)] // E_FAIL
    public void Hresults_map(int hr, bool signIn, PasskeyError expected) => Assert.Equal(expected, WebAuthnMarshal.MapError(hr, signIn));

    [Fact]
    public void Transport_names_round_trip()
    {
        var all = new[] { "ble", "hybrid", "internal", "nfc", "smart-card", "usb" };
        Assert.Equal(all, WebAuthnMarshal.TransportNames(WebAuthnMarshal.TransportBits(all)));
        Assert.Empty(WebAuthnMarshal.TransportNames(WebAuthnConst.CTAP_TRANSPORT_TEST));
        Assert.Equal(0u, WebAuthnMarshal.TransportBits(new[] { "cable" }));
    }

    [Fact]
    public void Unavailable_without_webauthn_dll()
    {
        if (OperatingSystem.IsWindows()) return;
#pragma warning disable CA1416 // Exercising the "no webauthn.dll" path off Windows on purpose.
        var w = new WindowsWebAuthn(() => IntPtr.Zero);
        Assert.False(w.IsAvailable);
        Assert.Equal(0u, w.ApiVersion);
        var ex = Assert.Throws<PasskeyException>(() => w.GetAssertionAsync(Request()).GetAwaiter().GetResult());
#pragma warning restore CA1416
        Assert.Equal(PasskeyError.Unavailable, ex.Error);
    }

    [Fact]
    public void Windows_reports_its_api_version()
    {
        if (!OperatingSystem.IsWindows()) return;
        var w = new WindowsWebAuthn(() => IntPtr.Zero);
        // Every supported Windows 10/11 and Server 2019+ ships webauthn.dll.
        Assert.True(w.IsAvailable, "webauthn.dll missing");
        Console.WriteLine("webauthn.dll API version " + w.ApiVersion);
    }
}
