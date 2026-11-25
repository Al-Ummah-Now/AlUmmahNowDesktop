#if UPDATE_TEST
namespace AlUmmahNow.CoreBridge.Native.SelfUpdate;

/// <summary>
/// CI's update test (scripts/update-test.mjs): every request goes to the local test server with its real host in the path, so
/// the host rules run on the real addresses and nothing leaves the machine. Only with -p:UpdateTest=true.
/// </summary>
internal sealed class UpdateTestHandler : HttpMessageHandler
{
    private static readonly Uri Server = new(CoreUpdateChecker.TestHook("AUN_UPDATE_TEST_SERVER"));
    private readonly HttpMessageInvoker _inner = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var real = request.RequestUri!;
        request.RequestUri = new Uri(Server, $"/{real.Host}{real.PathAndQuery}");
        return _inner.SendAsync(request, cancellationToken);
    }
}
#endif
