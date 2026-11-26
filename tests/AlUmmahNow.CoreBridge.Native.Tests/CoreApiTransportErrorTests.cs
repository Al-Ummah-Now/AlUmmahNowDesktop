using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Native.Studio;
using AlUmmahNow.CoreBridge.Studio;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>Core errors keep the server's code, so 404 feature_off and 402 hide entry points (owner rule 2026-11-16).</summary>
public sealed class CoreApiTransportErrorTests
{
    [Fact]
    public void NotFoundWithTheFeatureOffBodyIsFeatureOff()
    {
        // The core keeps a short body without a message as the detail.
        var e = Assert.IsType<StudioApiException>(CoreApiTransport.Map(new AunException.NotFound(null, "{\"success\":false,\"code\":\"feature_off\"}"), "get_AnalyticsChannelGet"));
        Assert.Equal(404, e.Status);
        Assert.Equal("feature_off", e.Code);
        Assert.True(e.IsFeatureOff);
        Assert.True(e.HidesFeature);
    }

    [Fact]
    public void NotFoundPreservesStructuredFeatureOffCode()
    {
        var e = Assert.IsType<StudioApiException>(CoreApiTransport.Map(
            new AunException.NotFound("feature_off", "Feature unavailable"), "get_AnalyticsChannelGet"));
        Assert.True(e.HidesFeature);
    }

    [Fact]
    public void PlainNotFoundIsNotFeatureOff()
    {
        var e = Assert.IsType<StudioApiException>(CoreApiTransport.Map(new AunException.NotFound(null, "Video not found"), "get_VideoFetch"));
        Assert.Equal(404, e.Status);
        Assert.Null(e.Code);
        Assert.False(e.HidesFeature);
    }

    [Fact]
    public void PaymentRequiredHides()
    {
        var e = Assert.IsType<StudioApiException>(CoreApiTransport.Map(new AunException.Http(402, "upgrade_required", "Upgrade required"), "post_VideoBulkUpdate"));
        Assert.Equal(402, e.Status);
        Assert.Equal("upgrade_required", e.Code);
        Assert.True(e.HidesFeature);
        Assert.False(e.IsFeatureOff);
    }

    [Fact]
    public void ForbiddenStaysForbidden()
    {
        var e = Assert.IsType<StudioApiException>(CoreApiTransport.Map(new AunException.Forbidden(null, "Only the channel owner"), "get_AnalyticsChannelGet"));
        Assert.Equal(403, e.Status);
        Assert.False(e.HidesFeature);
    }
}
