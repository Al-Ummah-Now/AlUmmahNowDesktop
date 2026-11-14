using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>Unpadded base64url (RFC 4648 §5), the encoding WebAuthn L3 JSON uses for bytes.</summary>
    public static class Base64Url
    {
        public static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var s = value.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Not base64url");
            }

            return Convert.FromBase64String(s);
        }
    }

    /// <summary>
    /// Maps between the server's WebAuthn L3 JSON (PublicKeyCredential*OptionsJSON in,
    /// AuthenticationResponseJSON / RegistrationResponseJSON out) and the raw bytes the platform
    /// API works with. Also builds clientDataJSON, which a native app writes itself.
    /// </summary>
    public static class WebAuthnJson
    {
        public const int DefaultTimeoutMs = 300_000;

        /// <summary>
        /// The origin a native app claims in clientDataJSON: <c>https://&lt;rpId&gt;</c>, so passkeys
        /// saved on the website work here and the server checks one origin per environment.
        /// </summary>
        public static string OriginFor(string rpId) => "https://" + rpId;

        public static PasskeyRequest ParseRequestOptions(JsonElement options)
        {
            var rpId = RequiredString(options, "rpId");
            return new PasskeyRequest(
                Base64Url.Decode(RequiredString(options, "challenge")),
                rpId,
                Timeout(options),
                OptionalString(options, "userVerification") ?? "preferred",
                Descriptors(options, "allowCredentials"));
        }

        public static PasskeyCreation ParseCreationOptions(JsonElement options)
        {
            var rp = RequiredObject(options, "rp");
            var user = RequiredObject(options, "user");
            var algorithms = new List<int>();
            if (options.TryGetProperty("pubKeyCredParams", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parameters.EnumerateArray())
                {
                    if (OptionalString(p, "type") is { } t && t != "public-key") continue;
                    if (p.TryGetProperty("alg", out var alg) && alg.ValueKind == JsonValueKind.Number && alg.TryGetInt32(out var a)) algorithms.Add(a);
                }
            }

            if (algorithms.Count == 0) throw Malformed("pubKeyCredParams");
            string residentKey = "discouraged", userVerification = "preferred";
            string? attachment = null;
            if (options.TryGetProperty("authenticatorSelection", out var selection) && selection.ValueKind == JsonValueKind.Object)
            {
                residentKey = OptionalString(selection, "residentKey")
                    ?? (selection.TryGetProperty("requireResidentKey", out var rrk) && rrk.ValueKind == JsonValueKind.True ? "required" : "discouraged");
                userVerification = OptionalString(selection, "userVerification") ?? userVerification;
                attachment = OptionalString(selection, "authenticatorAttachment");
            }

            var userName = RequiredString(user, "name");
            return new PasskeyCreation(
                Base64Url.Decode(RequiredString(options, "challenge")),
                OptionalString(rp, "id") ?? throw Malformed("rp.id"),
                OptionalString(rp, "name") ?? "",
                Base64Url.Decode(RequiredString(user, "id")),
                userName,
                OptionalString(user, "displayName") ?? userName,
                algorithms,
                Timeout(options),
                residentKey,
                userVerification,
                attachment,
                OptionalString(options, "attestation") ?? "none",
                Descriptors(options, "excludeCredentials"));
        }

        /// <summary>CollectedClientData for <c>webauthn.get</c> or <c>webauthn.create</c>, UTF-8.</summary>
        public static byte[] ClientData(string type, byte[] challenge, string rpId)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                w.WriteString("challenge", Base64Url.Encode(challenge));
                w.WriteString("origin", OriginFor(rpId));
                w.WriteBoolean("crossOrigin", false);
                w.WriteEndObject();
            }

            return stream.ToArray();
        }

        public static string AuthenticationResponse(PasskeyAssertion a)
        {
            return Write(w =>
            {
                var id = Base64Url.Encode(a.CredentialId);
                w.WriteString("id", id);
                w.WriteString("rawId", id);
                w.WriteString("type", "public-key");
                w.WriteStartObject("response");
                w.WriteString("clientDataJSON", Base64Url.Encode(a.ClientDataJson));
                w.WriteString("authenticatorData", Base64Url.Encode(a.AuthenticatorData));
                w.WriteString("signature", Base64Url.Encode(a.Signature));
                if (a.UserHandle is { Length: > 0 } handle) w.WriteString("userHandle", Base64Url.Encode(handle));
                w.WriteEndObject();
                w.WriteStartObject("clientExtensionResults");
                w.WriteEndObject();
                if (a.AuthenticatorAttachment != null) w.WriteString("authenticatorAttachment", a.AuthenticatorAttachment);
            });
        }

        public static string RegistrationResponse(PasskeyAttestation a)
        {
            return Write(w =>
            {
                var id = Base64Url.Encode(a.CredentialId);
                w.WriteString("id", id);
                w.WriteString("rawId", id);
                w.WriteString("type", "public-key");
                w.WriteStartObject("response");
                w.WriteString("clientDataJSON", Base64Url.Encode(a.ClientDataJson));
                w.WriteString("attestationObject", Base64Url.Encode(a.AttestationObject));
                if (a.AuthenticatorData.Length > 0) w.WriteString("authenticatorData", Base64Url.Encode(a.AuthenticatorData));
                w.WriteStartArray("transports");
                foreach (var t in a.Transports) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartObject("clientExtensionResults");
                w.WriteEndObject();
                if (a.AuthenticatorAttachment != null) w.WriteString("authenticatorAttachment", a.AuthenticatorAttachment);
            });
        }

        private static string Write(Action<Utf8JsonWriter> body)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static int Timeout(JsonElement options) =>
            options.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var ms) && ms > 0 ? ms : DefaultTimeoutMs;

        private static IReadOnlyList<PasskeyDescriptor> Descriptors(JsonElement options, string name)
        {
            var list = new List<PasskeyDescriptor>();
            if (!options.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in items.EnumerateArray())
            {
                if (OptionalString(item, "type") is { } t && t != "public-key") continue;
                var transports = new List<string>();
                if (item.TryGetProperty("transports", out var ts) && ts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tr in ts.EnumerateArray())
                    {
                        if (tr.ValueKind == JsonValueKind.String) transports.Add(tr.GetString()!);
                    }
                }

                list.Add(new PasskeyDescriptor(Base64Url.Decode(RequiredString(item, "id")), transports));
            }

            return list;
        }

        private static string RequiredString(JsonElement e, string name) => OptionalString(e, name) ?? throw Malformed(name);

        private static JsonElement RequiredObject(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw Malformed(name);

        private static string? OptionalString(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static PasskeyException Malformed(string field) => new PasskeyException(PasskeyError.Failed, "Passkey options have no valid " + field);
    }
}
