using System.Net;
using AlUmmahNow.Admin.Review;
using Xunit;

namespace AlUmmahNow.Admin.Tests;

public sealed class InjectionAuditTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{}}") });
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public async Task AdminApiReviewService_rejects_dot_segment_ids_without_network_call(string badId)
    {
        var handler = new CountingHandler();
        var service = new AdminApiReviewService(
            new Uri("https://admin.api.example.net"),
            new Uri("https://videos.api.example.net"),
            _ => Task.FromResult<string?>("secret-tok"),
            "AlUmmahNowAdmin/1.0",
            new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ClaimAsync(badId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DetailAsync(badId));
        Assert.Equal(0, handler.Calls);
    }
}
