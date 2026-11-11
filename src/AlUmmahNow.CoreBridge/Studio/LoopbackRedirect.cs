using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlUmmahNow.CoreBridge.Studio
{
    /// <summary>
    /// Receives the OAuth redirect on 127.0.0.1 with a port the OS picks (RFC 8252 §7.3). Answers
    /// the browser with a short bilingual page and returns the full redirect URL.
    /// </summary>
    public sealed class LoopbackRedirect : IDisposable
    {
        public const string CallbackPath = "/auth/callback";

        private readonly TcpListener _listener;
        private readonly string _successHtml;

        public LoopbackRedirect(string successMessage)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _successHtml = "<!doctype html><meta charset=utf-8><title>Al Ummah Now Studio</title>"
                + "<body style=\"font-family:sans-serif;text-align:center;margin-top:20vh\"><p dir=auto>"
                + WebUtility.HtmlEncode(successMessage) + "</p>";
        }

        public int Port { get; }

        public string RedirectUri => "http://127.0.0.1:" + Port + CallbackPath;

        /// <summary>Waits for the browser to hit the callback; other paths (favicon) get 404.</summary>
        public async Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() => _listener.Stop()))
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
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
                        var parts = line?.Split(' ');
                        var path = parts != null && parts.Length >= 2 ? parts[1] : "";
                        if (!path.StartsWith(CallbackPath, StringComparison.Ordinal))
                        {
                            await WriteAsync(stream, "404 Not Found", "", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        await WriteAsync(stream, "200 OK", _successHtml, cancellationToken).ConfigureAwait(false);
                        return "http://127.0.0.1:" + Port + path;
                    }
                }
            }
        }

        public void Dispose() => _listener.Stop();

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
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }
    }
}
