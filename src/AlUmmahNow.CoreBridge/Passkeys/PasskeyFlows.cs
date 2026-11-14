using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>The PKCE parameters of an authorize URL the core built (it keeps the verifier).</summary>
    public sealed class AuthorizeParameters
    {
        private AuthorizeParameters(string clientId, string redirectUri, string state, string codeChallenge)
        {
            ClientId = clientId;
            RedirectUri = redirectUri;
            State = state;
            CodeChallenge = codeChallenge;
        }

        public string ClientId { get; }
        public string RedirectUri { get; }
        public string State { get; }
        public string CodeChallenge { get; }

        public static AuthorizeParameters Parse(string authorizeUrl)
        {
            var uri = new Uri(authorizeUrl);
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in uri.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                query[key] = value;
            }

            string Get(string name) => query.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new PasskeyException(PasskeyError.Failed, "Authorize URL has no " + name);
            if (query.TryGetValue("code_challenge_method", out var method) && method != "S256") throw new PasskeyException(PasskeyError.Failed, "PKCE method is not S256");
            return new AuthorizeParameters(Get("client_id"), Get("redirect_uri"), Get("state"), Get("code_challenge"));
        }

        /// <summary>The redirect the core's CompleteLogin expects, carrying the passkey's code.</summary>
        public string CallbackWith(string code) =>
            RedirectUri + (RedirectUri.IndexOf('?') < 0 ? "?" : "&") + "code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(State);
    }

    /// <summary>
    /// Native passkey ceremonies: sign-in turns the platform's assertion into an authorization code
    /// for the existing PKCE token exchange (one session code path); "Add a passkey" re-signs in once
    /// when the server asks for a recent sign-in and tries again.
    /// </summary>
    public sealed class PasskeyFlows
    {
        private readonly NativePasskeyClient _client;
        private readonly IWebAuthn _webAuthn;

        public PasskeyFlows(NativePasskeyClient client, IWebAuthn webAuthn)
        {
            _client = client;
            _webAuthn = webAuthn;
        }

        public bool IsAvailable => _webAuthn.IsAvailable;

        /// <summary>Runs the passkey sign-in for the authorize URL the core built.</summary>
        /// <returns>The redirect URL to hand to the core's CompleteLogin.</returns>
        public async Task<string> SignInAsync(string authorizeUrl, CancellationToken cancellationToken = default)
        {
            if (!_webAuthn.IsAvailable) throw new PasskeyException(PasskeyError.Unavailable, "No platform passkey API");
            var p = AuthorizeParameters.Parse(authorizeUrl);
            var challenge = await _client.SignInOptionsAsync(p.ClientId, p.CodeChallenge, p.RedirectUri, cancellationToken).ConfigureAwait(false);
            var assertion = await _webAuthn.GetAssertionAsync(challenge.Options, cancellationToken).ConfigureAwait(false);
            var code = await _client.SignInVerifyAsync(challenge.Id, p.ClientId, WebAuthnJson.AuthenticationResponse(assertion), cancellationToken).ConfigureAwait(false);
            return p.CallbackWith(code);
        }

        /// <param name="accessToken">The signed-in account's access token (null: signed out).</param>
        /// <param name="reSignIn">A fresh interactive sign-in of the same account; false when cancelled.</param>
        public async Task<SavedPasskey> AddAsync(Func<CancellationToken, Task<string?>> accessToken, Func<CancellationToken, Task<bool>> reSignIn, string? name = null, CancellationToken cancellationToken = default)
        {
            if (!_webAuthn.IsAvailable) throw new PasskeyException(PasskeyError.Unavailable, "No platform passkey API");
            try
            {
                return await AddOnceAsync(accessToken, name, cancellationToken).ConfigureAwait(false);
            }
            catch (PasskeyException ex) when (ex.Error == PasskeyError.StepUpRequired)
            {
                if (!await reSignIn(cancellationToken).ConfigureAwait(false)) throw new PasskeyException(PasskeyError.Cancelled, "Re-sign-in was cancelled", ex);
            }

            // Once only: a second step_up_required surfaces as an error instead of a loop.
            return await AddOnceAsync(accessToken, name, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SavedPasskey> AddOnceAsync(Func<CancellationToken, Task<string?>> accessToken, string? name, CancellationToken cancellationToken)
        {
            var token = await accessToken(cancellationToken).ConfigureAwait(false)
                ?? throw new PasskeyException(PasskeyError.StepUpRequired, "Not signed in");
            var challenge = await _client.RegisterOptionsAsync(token, cancellationToken).ConfigureAwait(false);
            var attestation = await _webAuthn.MakeCredentialAsync(challenge.Options, cancellationToken).ConfigureAwait(false);
            return await _client.RegisterVerifyAsync(token, challenge.Id, WebAuthnJson.RegistrationResponse(attestation), name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Where the platform has no passkey API (Linux, Tizen, CI demo): buttons stay hidden.</summary>
    public sealed class UnavailableWebAuthn : IWebAuthn
    {
        public static readonly UnavailableWebAuthn Instance = new UnavailableWebAuthn();

        public bool IsAvailable => false;

        public Task<PasskeyAssertion> GetAssertionAsync(PasskeyRequest request, CancellationToken cancellationToken = default) =>
            throw new PasskeyException(PasskeyError.Unavailable, "No platform passkey API");

        public Task<PasskeyAttestation> MakeCredentialAsync(PasskeyCreation creation, CancellationToken cancellationToken = default) =>
            throw new PasskeyException(PasskeyError.Unavailable, "No platform passkey API");
    }
}
