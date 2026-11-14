using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>
    /// The issuer's native passkey endpoints (wiki AUTH-PASSKEYS, "Native apps"):
    /// <c>POST /native/passkey/options</c> and <c>/verify</c> turn a passkey into an authorization
    /// code for the app's PKCE flow; <c>/native/passkey/register/options</c> and <c>/verify</c>
    /// (Bearer) add a passkey to the signed-in account. Errors are <c>{error, error_description}</c>.
    /// </summary>
    public sealed class NativePasskeyClient
    {
        private readonly HttpClient _http;
        private readonly Uri _issuer;
        private readonly string? _userAgent;

        public NativePasskeyClient(HttpClient http, Uri issuer, string? userAgent = null)
        {
            _http = http;
            _issuer = new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/");
            _userAgent = userAgent;
        }

        public sealed class Challenge<T>
        {
            public Challenge(string id, T options)
            {
                Id = id;
                Options = options;
            }

            public string Id { get; }
            public T Options { get; }
        }

        public async Task<Challenge<PasskeyRequest>> SignInOptionsAsync(string clientId, string codeChallenge, string redirectUri, CancellationToken cancellationToken = default)
        {
            var body = Json(w =>
            {
                w.WriteString("client_id", clientId);
                w.WriteString("code_challenge", codeChallenge);
                w.WriteString("code_challenge_method", "S256");
                // Not in the first contract draft: lets the server bind the code to the redirect the
                // token exchange will send when a client registers more than one.
                w.WriteString("redirect_uri", redirectUri);
            });
            using var doc = await PostAsync("native/passkey/options", body, null, Stage.SignIn, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var options = WebAuthnJson.ParseRequestOptions(PublicKey(root));
            CheckRpId(options.RpId);
            return new Challenge<PasskeyRequest>(ChallengeId(root), options);
        }

        /// <returns>The authorization code.</returns>
        public async Task<string> SignInVerifyAsync(string challengeId, string clientId, string credentialJson, CancellationToken cancellationToken = default)
        {
            var body = Json(w =>
            {
                w.WriteString("challenge_id", challengeId);
                w.WriteString("client_id", clientId);
                w.WritePropertyName("credential");
                using var credential = JsonDocument.Parse(credentialJson);
                credential.RootElement.WriteTo(w);
            });
            using var doc = await PostAsync("native/passkey/verify", body, null, Stage.SignIn, cancellationToken).ConfigureAwait(false);
            return Str(doc.RootElement, "code") ?? throw new PasskeyException(PasskeyError.Failed, "Verify answer has no code");
        }

        public async Task<Challenge<PasskeyCreation>> RegisterOptionsAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            using var doc = await PostAsync("native/passkey/register/options", "{}", accessToken, Stage.Register, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var options = WebAuthnJson.ParseCreationOptions(PublicKey(root));
            CheckRpId(options.RpId);
            return new Challenge<PasskeyCreation>(ChallengeId(root), options);
        }

        public async Task<SavedPasskey> RegisterVerifyAsync(string accessToken, string challengeId, string credentialJson, string? name, CancellationToken cancellationToken = default)
        {
            var body = Json(w =>
            {
                w.WriteString("challenge_id", challengeId);
                w.WritePropertyName("credential");
                using (var credential = JsonDocument.Parse(credentialJson)) credential.RootElement.WriteTo(w);
                if (!string.IsNullOrWhiteSpace(name)) w.WriteString("name", name);
            });
            using var doc = await PostAsync("native/passkey/register/verify", body, accessToken, Stage.Register, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            DateTimeOffset? created = null;
            if (root.TryGetProperty("created_at", out var c))
            {
                if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var ms)) created = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                else if (c.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) created = parsed;
            }

            return new SavedPasskey(Str(root, "id") ?? throw new PasskeyException(PasskeyError.Failed, "Register answer has no id"), Str(root, "name") ?? "", created);
        }

        /// <summary>
        /// A native app may claim any origin, so the RP id is pinned to the issuer: its host or a
        /// parent domain on a label boundary (auth.qa.alummahnow.net → qa.alummahnow.net or
        /// alummahnow.net), never a bare TLD. Anything else would let a tampered answer make the app
        /// sign for another site.
        /// </summary>
        public static bool IsAllowedRpId(Uri issuer, string rpId)
        {
            var host = issuer.IdnHost.TrimEnd('.').ToLowerInvariant();
            var rp = (rpId ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (rp.Length == 0 || rp.IndexOf('.') < 0 && rp != "localhost" && host != rp) return false;
            return host == rp || host.EndsWith("." + rp, StringComparison.Ordinal);
        }

        private void CheckRpId(string rpId)
        {
            if (!IsAllowedRpId(_issuer, rpId)) throw new PasskeyException(PasskeyError.Failed, "Passkey options name a relying party outside the issuer's domain");
        }

        private enum Stage { SignIn, Register }

        private async Task<JsonDocument> PostAsync(string path, string json, string? bearer, Stage stage, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_issuer, path))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_userAgent != null) request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new PasskeyException(PasskeyError.Offline, "Passkey request failed to send", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PasskeyException(PasskeyError.Offline, "Passkey request timed out", ex);
            }

            using (response)
            {
                string text;
                try
                {
                    text = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    throw new PasskeyException(PasskeyError.Offline, "Passkey answer was cut off", ex);
                }

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return JsonDocument.Parse(text);
                    }
                    catch (JsonException ex)
                    {
                        throw new PasskeyException(PasskeyError.Failed, "Passkey answer is not JSON", ex);
                    }
                }

                throw MapError(response, text, stage);
            }
        }

        private static PasskeyException MapError(HttpResponseMessage response, string body, Stage stage)
        {
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                error = Str(doc.RootElement, "error");
            }
            catch (JsonException)
            {
                // Not JSON (a proxy page): the status decides.
            }

            var status = (int)response.StatusCode;
            var message = "Passkey request failed: HTTP " + status + (error != null ? " " + error : "");
            if (status == 429) return new PasskeyException(PasskeyError.RateLimited, message, serverError: error, retryAfter: RetryAfter(response));
            if (error == "step_up_required") return new PasskeyException(PasskeyError.StepUpRequired, message, serverError: error);
            if (error == "passkey_limit" || error == "limit_reached" || error == "too_many_passkeys") return new PasskeyException(PasskeyError.Limit, message, serverError: error);
            if (error == "credential_exists" || error == "already_registered") return new PasskeyException(PasskeyError.AlreadyRegistered, message, serverError: error);
            if (status >= 500) return new PasskeyException(PasskeyError.Failed, message, serverError: error);
            // 400/401/403/404 on a ceremony: the passkey, challenge or account was not accepted.
            return new PasskeyException(stage == Stage.SignIn || status == 400 ? PasskeyError.Rejected : PasskeyError.Failed, message, serverError: error);
        }

        private static TimeSpan RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta is { } delta) return delta;
            if (header?.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(30);
        }

        private static string ChallengeId(JsonElement root) =>
            Str(root, "challenge_id") ?? throw new PasskeyException(PasskeyError.Failed, "Options answer has no challenge_id");

        private static JsonElement PublicKey(JsonElement root) =>
            root.TryGetProperty("publicKey", out var pk) && pk.ValueKind == JsonValueKind.Object ? pk : throw new PasskeyException(PasskeyError.Failed, "Options answer has no publicKey");

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Json(Action<Utf8JsonWriter> body)
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
    }
}
