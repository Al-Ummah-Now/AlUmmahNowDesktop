using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.Studio.Upload;

/// <summary>Chooses the channel uploads go to: the account's main channel, else its first.</summary>
public sealed class UploadContext
{
    private readonly IChannelClient _channels;
    private ChannelInfo? _channel;

    public UploadContext(IChannelClient channels) => _channels = channels;

    public ChannelInfo? Channel => _channel;

    public void Reset() => _channel = null;

    public async Task<string> ChannelIdAsync(CancellationToken cancellationToken = default)
    {
        var channel = _channel ?? await LoadAsync(cancellationToken);
        return channel?.Id ?? throw new InvalidOperationException("This account has no channel");
    }

    public async Task<ChannelInfo?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var list = await _channels.ListMineAsync(cancellationToken);
        _channel = list.FirstOrDefault(c => c.IsMain) ?? list.FirstOrDefault();
        return _channel;
    }
}
