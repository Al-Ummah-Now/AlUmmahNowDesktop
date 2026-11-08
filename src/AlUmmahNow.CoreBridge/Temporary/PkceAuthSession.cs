using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Temporary
{
    /// <summary>
    /// TEMPORARY stand-in for aun-auth: OAuth 2 authorization code + PKCE S256 through the system
    /// browser and a loopback redirect (RFC 8252). Replace when AlUmmahNowCore exposes AuthManager.
    /// </summary>
    public sealed class PkceAuthSession : IAuthSession
    {
        private const string StoreKey = "auth.session";
        private const string Scopes = "openid profile email offline_access";
        private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

        private readonly HttpClient _http;
        private readonly Uri _issuer;
        private readonly string _clientId;
        private readonly ISecureStore _store;
        private readonly IBrowserLauncher _browser;
        private readonly string _successHtml;
        private readonly TimeSpan _signInTimeout;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private Tokens? _tokens;

        public PkceAuthSession(HttpClient http, Uri issuer, string clientId, ISecureStore store, IBrowserLauncher browser, string successMessage, TimeSpan? signInTimeout = null)
        {
            _http = http;
            _issuer = issuer;
            _clientId = clientId;
            _store = store;
            _browser = browser;
            _successHtml = "<!doctype html><meta charset=utf-8><title>Al Ummah Now</title><body style=\"font-family:sans-serif;text-align:center;margin-top:20vh\"><p dir=auto>"
                + WebUtility.HtmlEncode(successMessage) + "</p>";
            _signInTimeout = signInTimeout ?? TimeSpan.FromMinutes(5);
        }

        public event EventHandler? AccountChanged;

        public AuthAccount? Account { get; private set; }

        public void Restore()
        {
            try
            {
                var data = _store.Get(StoreKey);
                if (data == null) return;
                _tokens = JsonSerializer.Deserialize<Tokens>(data);
            }
            catch (JsonException)
            {
                _tokens = null;
            }
            catch (IOException)
            {
                _tokens = null;
            }

            SetAccount(_tokens == null ? null : ReadAccount(_tokens.Access));
        }

        public async Task SignInAsync(CancellationToken cancellationToken = default)
        {
            var verifier = Base64Url(RandomBytes(32));
            var state = Base64Url(RandomBytes(16));
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_signInTimeout);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var redirect = "http://127.0.0.1:" + port + "/auth/callback";
                _browser.Open(new Uri(_issuer, "/authorize?client_id=" + Uri.EscapeDataString(_clientId)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_type=code&scope=" + Uri.EscapeDataString(Scopes)
                    + "&state=" + state
                    + "&code_challenge=" + challenge
                    + "&code_challenge_method=S256"));

                var query = await WaitForCallbackAsync(listener, timeout.Token).ConfigureAwait(false);
                if (query.TryGetValue("error", out var error)) throw new AuthFailedException("Authorization failed: " + error);
                if (!query.TryGetValue("state", out var returned) || returned != state) throw new AuthFailedException("State mismatch");
                if (!query.TryGetValue("code", out var code)) throw new AuthFailedException("No authorization code");

                var tokens = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = _clientId,
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                    ["code_verifier"] = verifier,
                }, null, timeout.Token).ConfigureAwait(false);
                if (tokens == null) throw new AuthFailedException("The authorization code was rejected");
                Accept(tokens);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AuthFailedException("Sign-in timed out", ex);
            }
            finally
            {
                listener.Stop();
            }
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var seen = _tokens ?? throw new AuthRequiredException("Not signed in");
            if (seen.ExpiresAt - ExpirySkew > DateTimeOffset.UtcNow) return seen.Access;

            // One refresh at a time: rotation invalidates the old refresh token on first use.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = _tokens ?? throw new AuthRequiredException("Not signed in");
                if (current.ExpiresAt - ExpirySkew > DateTimeOffset.UtcNow) return current.Access;
                if (string.IsNullOrEmpty(current.Refresh))
                {
                    Clear();
                    throw new AuthRequiredException("Session expired");
                }

                var tokens = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId,
                    ["refresh_token"] = current.Refresh!,
                }, current.Refresh, cancellationToken).ConfigureAwait(false);
                if (tokens == null)
                {
                    Clear();
                    throw new AuthRequiredException("Session expired");
                }

                Accept(tokens);
                return tokens.Access;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void SignOut() => Clear();

        private void Accept(Tokens tokens)
        {
            _tokens = tokens;
            try
            {
                _store.Set(StoreKey, JsonSerializer.SerializeToUtf8Bytes(tokens));
            }
            catch (IOException)
            {
                // No keyring available: the session lasts until the app closes.
            }

            SetAccount(ReadAccount(tokens.Access));
        }

        private void Clear()
        {
            _tokens = null;
            try
            {
                _store.Delete(StoreKey);
            }
            catch (IOException)
            {
                // Nothing further can be done; the in-memory session is already gone.
            }

            SetAccount(null);
        }

        private void SetAccount(AuthAccount? account)
        {
            if (Account?.Subject == account?.Subject && Account?.Email == account?.Email) return;
            Account = account;
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <returns>The tokens, or null when the server rejected the grant.</returns>
        private async Task<Tokens?> RequestTokensAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(new Uri(_issuer, "/token"), new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new CatalogException("Network error", ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 400 || status == 401) return null;
                if (!response.IsSuccessStatusCode) throw new CatalogException("Token endpoint returned HTTP " + status);

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var root = doc.RootElement;
                var access = root.GetProperty("access_token").GetString() ?? throw new CatalogException("No access token");
                var expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 900;
                var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : previousRefresh;
                return new Tokens { Access = access, Refresh = refresh, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires) };
            }
        }

        private async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(listener.Stop))
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new AuthFailedException("The local callback listener stopped", ex);
                    }

                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var line = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
                        var path = line?.Split(' ').Length >= 2 ? line.Split(' ')[1] : "";
                        if (!path.StartsWith("/auth/callback", StringComparison.Ordinal))
                        {
                            // Browsers also ask for /favicon.ico; keep waiting for the real callback.
                            await WriteAsync(stream, "404 Not Found", "", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await WriteAsync(stream, "200 OK", _successHtml, cancellationToken).ConfigureAwait(false);
                        return ParseQuery(path);
                    }
                }
            }
        }

        private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.Contains("\r\n\r\n")) return text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
            }

            return null;
        }

        private static async Task WriteAsync(NetworkStream stream, string status, string body, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        private static Dictionary<string, string> ParseQuery(string path)
        {
            var result = new Dictionary<string, string>();
            var mark = path.IndexOf('?');
            if (mark < 0) return result;
            foreach (var pair in path.Substring(mark + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) result[pair.Substring(0, eq)] = WebUtility.UrlDecode(pair.Substring(eq + 1));
            }

            return result;
        }

        private static AuthAccount? ReadAccount(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return new AuthAccount("unknown", null);
            try
            {
                var padded = parts[1].Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
                var root = doc.RootElement;
                var props = root.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : root;
                var subject = Text(root, "sub") ?? Text(props, "id") ?? "unknown";
                return new AuthAccount(subject, Text(root, "email") ?? Text(props, "email"));
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException)
            {
                return new AuthAccount("unknown", null);
            }
        }

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed class Tokens
        {
            public string Access { get; set; } = "";
            public string? Refresh { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
