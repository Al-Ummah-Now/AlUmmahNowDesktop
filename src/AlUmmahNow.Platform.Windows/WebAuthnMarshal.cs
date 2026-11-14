using System.Runtime.InteropServices;
using AlUmmahNow.CoreBridge.Passkeys;

namespace AlUmmahNow.Platform.Windows.WebAuthn;

/// <summary>Unmanaged memory for one ceremony, freed together. Zero-filled.</summary>
internal sealed class NativeArena : IDisposable
{
    private readonly List<IntPtr> _blocks = new();

    public IntPtr Alloc(int size)
    {
        var length = Math.Max(size, 1);
        var p = Marshal.AllocHGlobal(length);
        Marshal.Copy(new byte[length], 0, p, length);

        _blocks.Add(p);
        return p;
    }

    public IntPtr Bytes(byte[] bytes)
    {
        var p = Alloc(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    public IntPtr WString(string value)
    {
        var p = Marshal.StringToHGlobalUni(value);
        _blocks.Add(p);
        return p;
    }

    public IntPtr Struct<T>(T value)
        where T : struct
    {
        var p = Alloc(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, false);
        return p;
    }

    /// <summary>A contiguous C array of structs.</summary>
    public IntPtr Array<T>(IReadOnlyList<T> items)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var p = Alloc(size * items.Count);
        for (var i = 0; i < items.Count; i++) Marshal.StructureToPtr(items[i], p + (i * size), false);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _blocks) Marshal.FreeHGlobal(p);
        _blocks.Clear();
    }
}

/// <summary>The native arguments for WebAuthNAuthenticatorGetAssertion.</summary>
internal readonly record struct AssertionCall(IntPtr RpId, IntPtr ClientData, IntPtr Options, byte[] ClientDataJson);

/// <summary>The native arguments for WebAuthNAuthenticatorMakeCredential.</summary>
internal readonly record struct MakeCredentialCall(IntPtr Rp, IntPtr User, IntPtr Parameters, IntPtr ClientData, IntPtr Options, byte[] ClientDataJson);

/// <summary>
/// Builds webauthn.dll arguments from decoded server options and reads its answers back, with no
/// OS calls, so the marshalling is unit-tested on any 64-bit runner.
/// </summary>
internal static class WebAuthnMarshal
{
    /// <summary>The newest GET_ASSERTION_OPTIONS version the installed API reads (webauthn.h version table).</summary>
    public static uint GetAssertionOptionsVersion(uint api) => api switch
    {
        >= 9 => 9,
        8 => 8,
        7 => 7,
        >= 4 => 6,
        3 => 5,
        _ => 4,
    };

    /// <summary>The newest MAKE_CREDENTIAL_OPTIONS version the installed API reads.</summary>
    public static uint MakeCredentialOptionsVersion(uint api) => api switch
    {
        >= 9 => 9,
        8 => 8,
        7 => 7,
        6 => 6,
        >= 4 => 5,
        3 => 4,
        _ => 3,
    };

    public static AssertionCall BuildAssertion(NativeArena arena, PasskeyRequest request, uint api, IntPtr cancellationId)
    {
        var clientDataJson = WebAuthnJson.ClientData("webauthn.get", request.Challenge, request.RpId);
        var version = GetAssertionOptionsVersion(api);
        var options = new WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS
        {
            dwVersion = version,
            dwTimeoutMilliseconds = (uint)request.TimeoutMs,
            dwAuthenticatorAttachment = WebAuthnConst.AUTHENTICATOR_ATTACHMENT_ANY,
            dwUserVerificationRequirement = UserVerification(request.UserVerification),
            pCancellationId = version >= 3 ? cancellationId : IntPtr.Zero,
        };
        if (request.AllowCredentials.Count > 0)
        {
            // v4+: the list with transports, so a phone or key is offered only where it can help.
            options.pAllowCredentialList = CredentialList(arena, request.AllowCredentials);
            options.CredentialList = Credentials(arena, request.AllowCredentials);
        }

        return new AssertionCall(arena.WString(request.RpId), ClientData(arena, clientDataJson), arena.Struct(options), clientDataJson);
    }

    public static MakeCredentialCall BuildMakeCredential(NativeArena arena, PasskeyCreation creation, uint api, IntPtr cancellationId)
    {
        var clientDataJson = WebAuthnJson.ClientData("webauthn.create", creation.Challenge, creation.RpId);
        var version = MakeCredentialOptionsVersion(api);
        var rp = new WEBAUTHN_RP_ENTITY_INFORMATION
        {
            dwVersion = WebAuthnConst.RP_ENTITY_INFORMATION_CURRENT_VERSION,
            pwszId = arena.WString(creation.RpId),
            pwszName = arena.WString(creation.RpName.Length > 0 ? creation.RpName : creation.RpId),
        };
        var user = new WEBAUTHN_USER_ENTITY_INFORMATION
        {
            dwVersion = WebAuthnConst.USER_ENTITY_INFORMATION_CURRENT_VERSION,
            cbId = (uint)creation.UserId.Length,
            pbId = arena.Bytes(creation.UserId),
            pwszName = arena.WString(creation.UserName),
            pwszDisplayName = arena.WString(creation.UserDisplayName),
        };
        var type = arena.WString(WebAuthnConst.CREDENTIAL_TYPE_PUBLIC_KEY);
        var parameters = creation.Algorithms.Select(a => new WEBAUTHN_COSE_CREDENTIAL_PARAMETER
        {
            dwVersion = WebAuthnConst.COSE_CREDENTIAL_PARAMETER_CURRENT_VERSION,
            pwszCredentialType = type,
            lAlg = a,
        }).ToList();
        var paramList = new WEBAUTHN_COSE_CREDENTIAL_PARAMETERS { cCredentialParameters = (uint)parameters.Count, pCredentialParameters = arena.Array(parameters) };
        var residentRequired = creation.ResidentKey == "required";
        var options = new WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS
        {
            dwVersion = version,
            dwTimeoutMilliseconds = (uint)creation.TimeoutMs,
            dwAuthenticatorAttachment = creation.AuthenticatorAttachment switch
            {
                "platform" => WebAuthnConst.AUTHENTICATOR_ATTACHMENT_PLATFORM,
                "cross-platform" => WebAuthnConst.AUTHENTICATOR_ATTACHMENT_CROSS_PLATFORM,
                _ => WebAuthnConst.AUTHENTICATOR_ATTACHMENT_ANY,
            },
            bRequireResidentKey = residentRequired ? 1 : 0,
            dwUserVerificationRequirement = UserVerification(creation.UserVerification),
            dwAttestationConveyancePreference = creation.Attestation switch
            {
                "direct" or "enterprise" => WebAuthnConst.ATTESTATION_CONVEYANCE_PREFERENCE_DIRECT,
                "indirect" => WebAuthnConst.ATTESTATION_CONVEYANCE_PREFERENCE_INDIRECT,
                _ => WebAuthnConst.ATTESTATION_CONVEYANCE_PREFERENCE_NONE,
            },
            pCancellationId = version >= 2 ? cancellationId : IntPtr.Zero,
            bPreferResidentKey = version >= 4 && creation.ResidentKey == "preferred" ? 1 : 0,
        };
        if (creation.ExcludeCredentials.Count > 0)
        {
            options.CredentialList = Credentials(arena, creation.ExcludeCredentials);
            options.pExcludeCredentialList = CredentialList(arena, creation.ExcludeCredentials);
        }

        return new MakeCredentialCall(arena.Struct(rp), arena.Struct(user), arena.Struct(paramList), ClientData(arena, clientDataJson), arena.Struct(options), clientDataJson);
    }

    /// <summary>Reads a WEBAUTHN_ASSERTION, touching only fields its dwVersion has.</summary>
    public static PasskeyAssertion ReadAssertion(IntPtr p, byte[] clientDataJson)
    {
        var version = (uint)Marshal.ReadInt32(p, Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.dwVersion)));
        var authData = Blob(p, Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.cbAuthenticatorData)), Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.pbAuthenticatorData)));
        var signature = Blob(p, Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.cbSignature)), Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.pbSignature)));
        var credential = Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.Credential));
        var credentialId = Blob(p, credential + Offset<WEBAUTHN_CREDENTIAL>(nameof(WEBAUTHN_CREDENTIAL.cbId)), credential + Offset<WEBAUTHN_CREDENTIAL>(nameof(WEBAUTHN_CREDENTIAL.pbId)));
        var userId = Blob(p, Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.cbUserId)), Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.pbUserId)));
        string? attachment = null;
        if (version >= 4) attachment = Attachment((uint)Marshal.ReadInt32(p, Offset<WEBAUTHN_ASSERTION>(nameof(WEBAUTHN_ASSERTION.dwUsedTransport))));
        if (credentialId.Length == 0 || authData.Length == 0 || signature.Length == 0) throw new PasskeyException(PasskeyError.Failed, "Assertion is missing fields");
        return new PasskeyAssertion(credentialId, clientDataJson, authData, signature, userId.Length > 0 ? userId : null, attachment);
    }

    /// <summary>Reads a WEBAUTHN_CREDENTIAL_ATTESTATION, touching only fields its dwVersion has.</summary>
    public static PasskeyAttestation ReadAttestation(IntPtr p, byte[] clientDataJson)
    {
        var version = (uint)Marshal.ReadInt32(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.dwVersion)));
        var authData = Blob(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.cbAuthenticatorData)), Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.pbAuthenticatorData)));
        var attestationObject = Blob(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.cbAttestationObject)), Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.pbAttestationObject)));
        var credentialId = Blob(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.cbCredentialId)), Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.pbCredentialId)));
        uint used = 0, supported = 0;
        if (version >= 3) used = (uint)Marshal.ReadInt32(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.dwUsedTransport)));
        if (version >= 8) supported = (uint)Marshal.ReadInt32(p, Offset<WEBAUTHN_CREDENTIAL_ATTESTATION>(nameof(WEBAUTHN_CREDENTIAL_ATTESTATION.dwTransports)));
        if (credentialId.Length == 0 || attestationObject.Length == 0) throw new PasskeyException(PasskeyError.Failed, "Attestation is missing fields");
        return new PasskeyAttestation(credentialId, clientDataJson, attestationObject, authData, TransportNames(supported != 0 ? supported : used), Attachment(used));
    }

    /// <summary>What an HRESULT from webauthn.dll means for the person.</summary>
    public static PasskeyError MapError(int hr, bool signIn) => hr switch
    {
        WebAuthnConst.NTE_USER_CANCELLED or WebAuthnConst.HRESULT_ERROR_CANCELLED or WebAuthnConst.HRESULT_ERROR_TIMEOUT => PasskeyError.Cancelled,
        WebAuthnConst.NTE_NOT_FOUND or WebAuthnConst.NTE_DEVICE_NOT_FOUND => signIn ? PasskeyError.NoPasskey : PasskeyError.Failed,
        WebAuthnConst.NTE_EXISTS => PasskeyError.AlreadyRegistered,
        WebAuthnConst.HRESULT_ERROR_NOT_SUPPORTED => PasskeyError.Unavailable,
        _ => PasskeyError.Failed,
    };

    /// <summary>WEBAUTHN_CTAP_TRANSPORT_* bits as WebAuthn transport names (test transport left out).</summary>
    public static IReadOnlyList<string> TransportNames(uint bits)
    {
        var names = new List<string>();
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_BLE) != 0) names.Add("ble");
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_HYBRID) != 0) names.Add("hybrid");
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_INTERNAL) != 0) names.Add("internal");
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_NFC) != 0) names.Add("nfc");
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_SMART_CARD) != 0) names.Add("smart-card");
        if ((bits & WebAuthnConst.CTAP_TRANSPORT_USB) != 0) names.Add("usb");
        return names;
    }

    public static uint TransportBits(IEnumerable<string> names)
    {
        uint bits = 0;
        foreach (var n in names)
        {
            bits |= n switch
            {
                "usb" => WebAuthnConst.CTAP_TRANSPORT_USB,
                "nfc" => WebAuthnConst.CTAP_TRANSPORT_NFC,
                "ble" => WebAuthnConst.CTAP_TRANSPORT_BLE,
                "internal" => WebAuthnConst.CTAP_TRANSPORT_INTERNAL,
                "hybrid" => WebAuthnConst.CTAP_TRANSPORT_HYBRID,
                "smart-card" => WebAuthnConst.CTAP_TRANSPORT_SMART_CARD,
                _ => 0u,
            };
        }

        return bits;
    }

    private static string? Attachment(uint usedTransport) =>
        usedTransport == 0 ? null : (usedTransport & WebAuthnConst.CTAP_TRANSPORT_INTERNAL) != 0 ? "platform" : "cross-platform";

    private static uint UserVerification(string value) => value switch
    {
        "required" => WebAuthnConst.USER_VERIFICATION_REQUIREMENT_REQUIRED,
        "discouraged" => WebAuthnConst.USER_VERIFICATION_REQUIREMENT_DISCOURAGED,
        _ => WebAuthnConst.USER_VERIFICATION_REQUIREMENT_PREFERRED,
    };

    private static IntPtr ClientData(NativeArena arena, byte[] json) => arena.Struct(new WEBAUTHN_CLIENT_DATA
    {
        dwVersion = WebAuthnConst.CLIENT_DATA_CURRENT_VERSION,
        cbClientDataJSON = (uint)json.Length,
        pbClientDataJSON = arena.Bytes(json),
        pwszHashAlgId = arena.WString(WebAuthnConst.HASH_ALGORITHM_SHA_256),
    });

    private static WEBAUTHN_CREDENTIALS Credentials(NativeArena arena, IReadOnlyList<PasskeyDescriptor> list)
    {
        var type = arena.WString(WebAuthnConst.CREDENTIAL_TYPE_PUBLIC_KEY);
        var items = list.Select(d => new WEBAUTHN_CREDENTIAL
        {
            dwVersion = WebAuthnConst.CREDENTIAL_CURRENT_VERSION,
            cbId = (uint)d.Id.Length,
            pbId = arena.Bytes(d.Id),
            pwszCredentialType = type,
        }).ToList();
        return new WEBAUTHN_CREDENTIALS { cCredentials = (uint)items.Count, pCredentials = arena.Array(items) };
    }

    private static IntPtr CredentialList(NativeArena arena, IReadOnlyList<PasskeyDescriptor> list)
    {
        var type = arena.WString(WebAuthnConst.CREDENTIAL_TYPE_PUBLIC_KEY);
        var pointers = arena.Alloc(IntPtr.Size * list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var d = list[i];
            var item = arena.Struct(new WEBAUTHN_CREDENTIAL_EX
            {
                dwVersion = WebAuthnConst.CREDENTIAL_EX_CURRENT_VERSION,
                cbId = (uint)d.Id.Length,
                pbId = arena.Bytes(d.Id),
                pwszCredentialType = type,
                dwTransports = TransportBits(d.Transports),
            });
            Marshal.WriteIntPtr(pointers, i * IntPtr.Size, item);
        }

        return arena.Struct(new WEBAUTHN_CREDENTIAL_LIST { cCredentials = (uint)list.Count, ppCredentials = pointers });
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

    private static byte[] Blob(IntPtr p, int countOffset, int pointerOffset)
    {
        var count = Marshal.ReadInt32(p, countOffset);
        var data = Marshal.ReadIntPtr(p, pointerOffset);
        if (count <= 0 || data == IntPtr.Zero) return System.Array.Empty<byte>();
        var bytes = new byte[count];
        Marshal.Copy(data, bytes, 0, count);
        return bytes;
    }
}
