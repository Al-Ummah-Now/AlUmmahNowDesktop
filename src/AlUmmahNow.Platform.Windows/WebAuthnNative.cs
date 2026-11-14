using System.Runtime.InteropServices;

#pragma warning disable IDE1006, SA1300, SA1307 // Names match webauthn.h exactly: the layout test looks them up by name.

namespace AlUmmahNow.Platform.Windows.WebAuthn;

// Blittable mirrors of webauthn.h (Windows SDK / github.com/microsoft/webauthn, API version 9).
// DWORD = uint, BOOL = int, LONG = int, pointers = IntPtr. Sequential layout gives the same
// natural alignment as MSVC on x64 and arm64; CI compares every size and offset with the SDK header.

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_RP_ENTITY_INFORMATION
{
    public uint dwVersion;
    public IntPtr pwszId;
    public IntPtr pwszName;
    public IntPtr pwszIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_USER_ENTITY_INFORMATION
{
    public uint dwVersion;
    public uint cbId;
    public IntPtr pbId;
    public IntPtr pwszName;
    public IntPtr pwszIcon;
    public IntPtr pwszDisplayName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CLIENT_DATA
{
    public uint dwVersion;
    public uint cbClientDataJSON;
    public IntPtr pbClientDataJSON;
    public IntPtr pwszHashAlgId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_COSE_CREDENTIAL_PARAMETER
{
    public uint dwVersion;
    public IntPtr pwszCredentialType;
    public int lAlg;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_COSE_CREDENTIAL_PARAMETERS
{
    public uint cCredentialParameters;
    public IntPtr pCredentialParameters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CREDENTIAL
{
    public uint dwVersion;
    public uint cbId;
    public IntPtr pbId;
    public IntPtr pwszCredentialType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CREDENTIALS
{
    public uint cCredentials;
    public IntPtr pCredentials;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CREDENTIAL_EX
{
    public uint dwVersion;
    public uint cbId;
    public IntPtr pbId;
    public IntPtr pwszCredentialType;
    public uint dwTransports;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CREDENTIAL_LIST
{
    public uint cCredentials;
    public IntPtr ppCredentials;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_EXTENSIONS
{
    public uint cExtensions;
    public IntPtr pExtensions;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_AUTHENTICATOR_MAKE_CREDENTIAL_OPTIONS
{
    public uint dwVersion;
    public uint dwTimeoutMilliseconds;
    public WEBAUTHN_CREDENTIALS CredentialList;
    public WEBAUTHN_EXTENSIONS Extensions;
    public uint dwAuthenticatorAttachment;
    public int bRequireResidentKey;
    public uint dwUserVerificationRequirement;
    public uint dwAttestationConveyancePreference;
    public uint dwFlags;

    // v2
    public IntPtr pCancellationId;

    // v3
    public IntPtr pExcludeCredentialList;

    // v4
    public uint dwEnterpriseAttestation;
    public uint dwLargeBlobSupport;
    public int bPreferResidentKey;

    // v5
    public int bBrowserInPrivateMode;

    // v6
    public int bEnablePrf;

    // v7
    public IntPtr pLinkedDevice;
    public uint cbJsonExt;
    public IntPtr pbJsonExt;

    // v8
    public IntPtr pPRFGlobalEval;
    public uint cCredentialHints;
    public IntPtr ppwszCredentialHints;
    public int bThirdPartyPayment;

    // v9
    public IntPtr pwszRemoteWebOrigin;
    public uint cbPublicKeyCredentialCreationOptionsJSON;
    public IntPtr pbPublicKeyCredentialCreationOptionsJSON;
    public uint cbAuthenticatorId;
    public IntPtr pbAuthenticatorId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_AUTHENTICATOR_GET_ASSERTION_OPTIONS
{
    public uint dwVersion;
    public uint dwTimeoutMilliseconds;
    public WEBAUTHN_CREDENTIALS CredentialList;
    public WEBAUTHN_EXTENSIONS Extensions;
    public uint dwAuthenticatorAttachment;
    public uint dwUserVerificationRequirement;
    public uint dwFlags;

    // v2
    public IntPtr pwszU2fAppId;
    public IntPtr pbU2fAppId;

    // v3
    public IntPtr pCancellationId;

    // v4
    public IntPtr pAllowCredentialList;

    // v5
    public uint dwCredLargeBlobOperation;
    public uint cbCredLargeBlob;
    public IntPtr pbCredLargeBlob;

    // v6
    public IntPtr pHmacSecretSaltValues;
    public int bBrowserInPrivateMode;

    // v7
    public IntPtr pLinkedDevice;
    public int bAutoFill;
    public uint cbJsonExt;
    public IntPtr pbJsonExt;

    // v8
    public uint cCredentialHints;
    public IntPtr ppwszCredentialHints;

    // v9
    public IntPtr pwszRemoteWebOrigin;
    public uint cbPublicKeyCredentialRequestOptionsJSON;
    public IntPtr pbPublicKeyCredentialRequestOptionsJSON;
    public uint cbAuthenticatorId;
    public IntPtr pbAuthenticatorId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_CREDENTIAL_ATTESTATION
{
    public uint dwVersion;
    public IntPtr pwszFormatType;
    public uint cbAuthenticatorData;
    public IntPtr pbAuthenticatorData;
    public uint cbAttestation;
    public IntPtr pbAttestation;
    public uint dwAttestationDecodeType;
    public IntPtr pvAttestationDecode;
    public uint cbAttestationObject;
    public IntPtr pbAttestationObject;
    public uint cbCredentialId;
    public IntPtr pbCredentialId;

    // v2
    public WEBAUTHN_EXTENSIONS Extensions;

    // v3
    public uint dwUsedTransport;

    // v4
    public int bEpAtt;
    public int bLargeBlobSupported;
    public int bResidentKey;

    // v5
    public int bPrfEnabled;

    // v6
    public uint cbUnsignedExtensionOutputs;
    public IntPtr pbUnsignedExtensionOutputs;

    // v7
    public IntPtr pHmacSecret;
    public int bThirdPartyPayment;

    // v8
    public uint dwTransports;
    public uint cbClientDataJSON;
    public IntPtr pbClientDataJSON;
    public uint cbRegistrationResponseJSON;
    public IntPtr pbRegistrationResponseJSON;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WEBAUTHN_ASSERTION
{
    public uint dwVersion;
    public uint cbAuthenticatorData;
    public IntPtr pbAuthenticatorData;
    public uint cbSignature;
    public IntPtr pbSignature;
    public WEBAUTHN_CREDENTIAL Credential;
    public uint cbUserId;
    public IntPtr pbUserId;

    // v2
    public WEBAUTHN_EXTENSIONS Extensions;
    public uint cbCredLargeBlob;
    public IntPtr pbCredLargeBlob;
    public uint dwCredLargeBlobStatus;

    // v3
    public IntPtr pHmacSecret;

    // v4
    public uint dwUsedTransport;

    // v5
    public uint cbUnsignedExtensionOutputs;
    public IntPtr pbUnsignedExtensionOutputs;

    // v6
    public uint cbClientDataJSON;
    public IntPtr pbClientDataJSON;
    public uint cbAuthenticationResponseJSON;
    public IntPtr pbAuthenticationResponseJSON;
}

/// <summary>Constants from webauthn.h and winerror.h.</summary>
internal static class WebAuthnConst
{
    public const uint RP_ENTITY_INFORMATION_CURRENT_VERSION = 1;
    public const uint USER_ENTITY_INFORMATION_CURRENT_VERSION = 1;
    public const uint CLIENT_DATA_CURRENT_VERSION = 1;
    public const uint COSE_CREDENTIAL_PARAMETER_CURRENT_VERSION = 1;
    public const uint CREDENTIAL_CURRENT_VERSION = 1;
    public const uint CREDENTIAL_EX_CURRENT_VERSION = 1;

    public const string CREDENTIAL_TYPE_PUBLIC_KEY = "public-key";
    public const string HASH_ALGORITHM_SHA_256 = "SHA-256";

    public const uint AUTHENTICATOR_ATTACHMENT_ANY = 0;
    public const uint AUTHENTICATOR_ATTACHMENT_PLATFORM = 1;
    public const uint AUTHENTICATOR_ATTACHMENT_CROSS_PLATFORM = 2;

    public const uint USER_VERIFICATION_REQUIREMENT_ANY = 0;
    public const uint USER_VERIFICATION_REQUIREMENT_REQUIRED = 1;
    public const uint USER_VERIFICATION_REQUIREMENT_PREFERRED = 2;
    public const uint USER_VERIFICATION_REQUIREMENT_DISCOURAGED = 3;

    public const uint ATTESTATION_CONVEYANCE_PREFERENCE_ANY = 0;
    public const uint ATTESTATION_CONVEYANCE_PREFERENCE_NONE = 1;
    public const uint ATTESTATION_CONVEYANCE_PREFERENCE_INDIRECT = 2;
    public const uint ATTESTATION_CONVEYANCE_PREFERENCE_DIRECT = 3;

    public const uint CTAP_TRANSPORT_USB = 0x01;
    public const uint CTAP_TRANSPORT_NFC = 0x02;
    public const uint CTAP_TRANSPORT_BLE = 0x04;
    public const uint CTAP_TRANSPORT_TEST = 0x08;
    public const uint CTAP_TRANSPORT_INTERNAL = 0x10;
    public const uint CTAP_TRANSPORT_HYBRID = 0x20;
    public const uint CTAP_TRANSPORT_SMART_CARD = 0x40;

    public const int S_OK = 0;
    public const int NTE_EXISTS = unchecked((int)0x8009000F);
    public const int NTE_NOT_FOUND = unchecked((int)0x80090011);
    public const int NTE_TOKEN_KEYSET_STORAGE_FULL = unchecked((int)0x80090023);
    public const int NTE_INVALID_PARAMETER = unchecked((int)0x80090027);
    public const int NTE_NOT_SUPPORTED = unchecked((int)0x80090029);
    public const int NTE_DEVICE_NOT_FOUND = unchecked((int)0x80090035);
    public const int NTE_USER_CANCELLED = unchecked((int)0x80090036);
    public const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
    public const int HRESULT_ERROR_TIMEOUT = unchecked((int)0x800705B4);
    public const int HRESULT_ERROR_NOT_SUPPORTED = unchecked((int)0x80070032);
}

/// <summary>webauthn.dll exports (System32, Windows 10 1903 and later).</summary>
internal static partial class WebAuthnApi
{
    private const string Dll = "webauthn.dll";

    [DllImport(Dll, ExactSpelling = true)]
    public static extern uint WebAuthNGetApiVersionNumber();

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int WebAuthNAuthenticatorMakeCredential(
        IntPtr hWnd,
        IntPtr pRpInformation,
        IntPtr pUserInformation,
        IntPtr pPubKeyCredParams,
        IntPtr pWebAuthNClientData,
        IntPtr pWebAuthNMakeCredentialOptions,
        out IntPtr ppWebAuthNCredentialAttestation);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int WebAuthNAuthenticatorGetAssertion(
        IntPtr hWnd,
        IntPtr pwszRpId,
        IntPtr pWebAuthNClientData,
        IntPtr pWebAuthNGetAssertionOptions,
        out IntPtr ppWebAuthNAssertion);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern void WebAuthNFreeCredentialAttestation(IntPtr pWebAuthNCredentialAttestation);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern void WebAuthNFreeAssertion(IntPtr pWebAuthNAssertion);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int WebAuthNGetCancellationId(out Guid pCancellationId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern int WebAuthNCancelCurrentOperation(ref Guid pCancellationId);

    [DllImport(Dll, ExactSpelling = true)]
    public static extern IntPtr WebAuthNGetErrorName(int hr);
}
