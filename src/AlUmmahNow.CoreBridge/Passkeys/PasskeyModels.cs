using System;
using System.Collections.Generic;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>A credential named in allowCredentials / excludeCredentials.</summary>
    public sealed class PasskeyDescriptor
    {
        public PasskeyDescriptor(byte[] id, IReadOnlyList<string> transports)
        {
            Id = id;
            Transports = transports;
        }

        public byte[] Id { get; }

        /// <summary>WebAuthn transport names ("internal", "hybrid", "usb", "nfc", "ble", "smart-card").</summary>
        public IReadOnlyList<string> Transports { get; }
    }

    /// <summary>PublicKeyCredentialRequestOptionsJSON, decoded.</summary>
    public sealed class PasskeyRequest
    {
        public PasskeyRequest(byte[] challenge, string rpId, int timeoutMs, string userVerification, IReadOnlyList<PasskeyDescriptor> allowCredentials)
        {
            Challenge = challenge;
            RpId = rpId;
            TimeoutMs = timeoutMs;
            UserVerification = userVerification;
            AllowCredentials = allowCredentials;
        }

        public byte[] Challenge { get; }
        public string RpId { get; }
        public int TimeoutMs { get; }

        /// <summary>"required", "preferred" or "discouraged".</summary>
        public string UserVerification { get; }

        /// <summary>Empty for discoverable sign-in (the server's default).</summary>
        public IReadOnlyList<PasskeyDescriptor> AllowCredentials { get; }
    }

    /// <summary>PublicKeyCredentialCreationOptionsJSON, decoded.</summary>
    public sealed class PasskeyCreation
    {
        public PasskeyCreation(
            byte[] challenge,
            string rpId,
            string rpName,
            byte[] userId,
            string userName,
            string userDisplayName,
            IReadOnlyList<int> algorithms,
            int timeoutMs,
            string residentKey,
            string userVerification,
            string? authenticatorAttachment,
            string attestation,
            IReadOnlyList<PasskeyDescriptor> excludeCredentials)
        {
            Challenge = challenge;
            RpId = rpId;
            RpName = rpName;
            UserId = userId;
            UserName = userName;
            UserDisplayName = userDisplayName;
            Algorithms = algorithms;
            TimeoutMs = timeoutMs;
            ResidentKey = residentKey;
            UserVerification = userVerification;
            AuthenticatorAttachment = authenticatorAttachment;
            Attestation = attestation;
            ExcludeCredentials = excludeCredentials;
        }

        public byte[] Challenge { get; }
        public string RpId { get; }
        public string RpName { get; }
        public byte[] UserId { get; }
        public string UserName { get; }
        public string UserDisplayName { get; }

        /// <summary>COSE algorithm ids in the server's order of preference (-7 ES256, -257 RS256).</summary>
        public IReadOnlyList<int> Algorithms { get; }

        public int TimeoutMs { get; }

        /// <summary>"required", "preferred" or "discouraged".</summary>
        public string ResidentKey { get; }

        public string UserVerification { get; }

        /// <summary>"platform", "cross-platform" or null for any.</summary>
        public string? AuthenticatorAttachment { get; }

        /// <summary>"none", "indirect", "direct" or "enterprise".</summary>
        public string Attestation { get; }

        public IReadOnlyList<PasskeyDescriptor> ExcludeCredentials { get; }
    }

    /// <summary>What the authenticator returned for a sign-in, as raw bytes.</summary>
    public sealed class PasskeyAssertion
    {
        public PasskeyAssertion(byte[] credentialId, byte[] clientDataJson, byte[] authenticatorData, byte[] signature, byte[]? userHandle, string? authenticatorAttachment)
        {
            CredentialId = credentialId;
            ClientDataJson = clientDataJson;
            AuthenticatorData = authenticatorData;
            Signature = signature;
            UserHandle = userHandle;
            AuthenticatorAttachment = authenticatorAttachment;
        }

        public byte[] CredentialId { get; }
        public byte[] ClientDataJson { get; }
        public byte[] AuthenticatorData { get; }
        public byte[] Signature { get; }
        public byte[]? UserHandle { get; }

        /// <summary>"platform" or "cross-platform" when the platform said which.</summary>
        public string? AuthenticatorAttachment { get; }
    }

    /// <summary>What the authenticator returned for a new passkey, as raw bytes.</summary>
    public sealed class PasskeyAttestation
    {
        public PasskeyAttestation(byte[] credentialId, byte[] clientDataJson, byte[] attestationObject, byte[] authenticatorData, IReadOnlyList<string> transports, string? authenticatorAttachment)
        {
            CredentialId = credentialId;
            ClientDataJson = clientDataJson;
            AttestationObject = attestationObject;
            AuthenticatorData = authenticatorData;
            Transports = transports;
            AuthenticatorAttachment = authenticatorAttachment;
        }

        public byte[] CredentialId { get; }
        public byte[] ClientDataJson { get; }
        public byte[] AttestationObject { get; }
        public byte[] AuthenticatorData { get; }
        public IReadOnlyList<string> Transports { get; }
        public string? AuthenticatorAttachment { get; }
    }

    /// <summary>A passkey the server saved (register/verify answer).</summary>
    public sealed class SavedPasskey
    {
        public SavedPasskey(string id, string name, DateTimeOffset? createdAt)
        {
            Id = id;
            Name = name;
            CreatedAt = createdAt;
        }

        public string Id { get; }
        public string Name { get; }
        public DateTimeOffset? CreatedAt { get; }
    }

    public enum PasskeyError
    {
        /// <summary>The person closed the Windows security sheet or it timed out. Show nothing.</summary>
        Cancelled,

        /// <summary>No passkey for this site on this device, phone, key or password manager.</summary>
        NoPasskey,

        /// <summary>This passkey is already saved for the account (excludeCredentials matched).</summary>
        AlreadyRegistered,

        /// <summary>The platform has no passkey support (old Windows, Linux).</summary>
        Unavailable,

        /// <summary>The server didn't accept the passkey (removed, wrong account, bad signature).</summary>
        Rejected,

        /// <summary>Adding a passkey needs a sign-in from the last 15 minutes.</summary>
        StepUpRequired,

        /// <summary>Too many attempts; <see cref="PasskeyException.RetryAfter"/> says how long to wait.</summary>
        RateLimited,

        /// <summary>The account already has the most passkeys allowed.</summary>
        Limit,

        /// <summary>No connection to the server.</summary>
        Offline,

        /// <summary>Anything else (server error, malformed answer, platform failure).</summary>
        Failed,
    }

    /// <summary>A passkey ceremony or server call did not finish. Never carries secrets.</summary>
    public sealed class PasskeyException : Exception
    {
        public PasskeyException(PasskeyError error, string message, Exception? inner = null, string? serverError = null, TimeSpan? retryAfter = null)
            : base(message, inner)
        {
            Error = error;
            ServerError = serverError;
            RetryAfter = retryAfter;
        }

        public PasskeyError Error { get; }

        /// <summary>The server's <c>error</c> code, when it sent one.</summary>
        public string? ServerError { get; }

        public TimeSpan? RetryAfter { get; }
    }
}
