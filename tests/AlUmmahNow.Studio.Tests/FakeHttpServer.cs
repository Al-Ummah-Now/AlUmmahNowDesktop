using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlUmmahNow.Studio.Tests;

/// <summary>A local HTTP server for API client tests: records every request and answers from a route table.</summary>
public sealed class FakeHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public sealed record Seen(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body, long? ContentLength)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    public sealed record Answer(int Status, string Body, string ContentType = "application/json")
    {
        public static Answer Json(int status, string body) => new(status, body);
    }

    public FakeHttpServer()
    {
        var port = FreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.AbsoluteUri);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public Uri BaseUri { get; }

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>"METHOD /path" → answer. Unmatched requests get 404.</summary>
    public ConcurrentDictionary<string, Func<Seen, Answer>> Routes { get; } = new();

    public void On(string route, int status, string body) => Routes[route] = _ => Answer.Json(status, body);

    public IReadOnlyList<Seen> Calls(string route) => Requests.Where(r => r.Method + " " + r.Path == route).ToList();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            var seen = new Seen(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query, headers, ms.ToArray(), ctx.Request.ContentLength64 >= 0 && ctx.Request.HasEntityBody ? ctx.Request.ContentLength64 : null);
            Requests.Enqueue(seen);
            var answer = Routes.TryGetValue(seen.Method + " " + seen.Path, out var f) ? f(seen) : Answer.Json(404, "{\"success\":false,\"error\":\"not_found\"}");
            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = answer.ContentType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}
