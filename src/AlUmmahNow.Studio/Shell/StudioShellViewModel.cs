using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.Studio.Shell;

public enum ShellState
{
    /// <summary>No account on this device.</summary>
    SignedOut,

    /// <summary>The browser sign-in is open.</summary>
    SigningIn,

    /// <summary>Loading the active account's channels.</summary>
    Loading,

    /// <summary>The account must sign in again (refresh token rejected).</summary>
    NeedsReauth,

    /// <summary>The account has no channel it can work in.</summary>
    NoChannel,

    /// <summary>Channels could not be loaded; <see cref="StudioShellViewModel.ErrorKey"/> says why.</summary>
    Error,

    Ready,
}

/// <summary>
/// Accounts and the channel the Studio works in. Platform shells bind to it; every other Studio
/// page reads <see cref="ActiveChannel"/>. Raises property changes on the thread that created it.
/// </summary>
public sealed partial class StudioShellViewModel : ObservableObject
{
    private static readonly Logger Logger = Log.For("shell");

    private readonly IStudioAccounts _accounts;
    private readonly IStudioApi _api;
    private readonly SynchronizationContext? _ui;
    private int _loadGeneration;

    [ObservableProperty] private ShellState _state = ShellState.SignedOut;
    [ObservableProperty] private IReadOnlyList<StudioAccount> _accountList = Array.Empty<StudioAccount>();
    [ObservableProperty] private StudioAccount? _activeAccount;
    [ObservableProperty] private IReadOnlyList<StudioChannel> _channels = Array.Empty<StudioChannel>();
    [ObservableProperty] private StudioChannel? _activeChannel;

    /// <summary>An i18n key for the last failure, or null.</summary>
    [ObservableProperty] private string? _errorKey;

    public StudioShellViewModel(IStudioAccounts accounts, IStudioApi api)
    {
        _accounts = accounts;
        _api = api;
        _ui = SynchronizationContext.Current;
        _accounts.Changed += (_, _) => OnUi(SyncAccounts);
    }

    public bool IsSignedIn => ActiveAccount != null;

    /// <summary>Raised on the UI thread after the active channel changes (including to null).</summary>
    public event EventHandler? ChannelChanged;

    /// <summary>Reads saved accounts and loads the active account's channels.</summary>
    public Task InitializeAsync()
    {
        try
        {
            _accounts.Reload();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Logger.Warn("could not read saved accounts", ex);
        }

        SyncAccounts();
        return LoadChannelsAsync();
    }

    public async Task SignInAsync(SignInMode mode, CancellationToken cancellationToken = default)
    {
        if (State == ShellState.SigningIn) return;
        var before = State;
        State = ShellState.SigningIn;
        ErrorKey = null;
        try
        {
            await _accounts.SignInAsync(mode, cancellationToken);
            Logger.Info("signed in");
        }
        catch (OperationCanceledException)
        {
            State = before;
            return;
        }
        catch (AuthFailedException ex)
        {
            Logger.Warn("sign-in failed", ex);
            ErrorKey = "auth.failed";
            State = before == ShellState.SigningIn ? ShellState.SignedOut : before;
            return;
        }

        SyncAccounts();
        await LoadChannelsAsync();
    }

    public async Task SwitchAccountAsync(string userId)
    {
        if (ActiveAccount?.UserId == userId) return;
        _accounts.Switch(userId);
        SyncAccounts();
        await LoadChannelsAsync();
    }

    /// <summary>Signs the account out of this device and revokes it. The UI confirms first.</summary>
    public async Task SignOutAsync(string userId)
    {
        await _accounts.SignOutAsync(userId);
        Logger.Info("signed out");
        SyncAccounts();
        await LoadChannelsAsync();
    }

    public void SelectChannel(StudioChannel? channel)
    {
        if (channel != null && !Channels.Contains(channel)) return;
        if (ReferenceEquals(channel, ActiveChannel)) return;
        ActiveChannel = channel;
        if (channel != null)
        {
            try
            {
                _accounts.SetActiveChannel(channel.Id);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                Logger.Warn("could not remember the channel", ex);
            }
        }

        ChannelChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RetryAsync() => LoadChannelsAsync();

    /// <summary>Called when any page gets <see cref="AuthRequiredException"/>.</summary>
    public void ReportAuthRequired()
    {
        _accounts.Reload();
        SyncAccounts();
        State = ShellState.NeedsReauth;
    }

    private async Task LoadChannelsAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        var account = ActiveAccount;
        if (account == null)
        {
            Channels = Array.Empty<StudioChannel>();
            SetChannel(null);
            State = ShellState.SignedOut;
            return;
        }

        if (account.NeedsReauth)
        {
            State = ShellState.NeedsReauth;
            return;
        }

        State = ShellState.Loading;
        ErrorKey = null;
        IReadOnlyList<StudioChannel> channels;
        try
        {
            channels = await _api.ListMyChannelsAsync();
        }
        catch (AuthRequiredException)
        {
            if (generation != _loadGeneration) return;
            ReportAuthRequired();
            return;
        }
        catch (StudioApiException ex)
        {
            if (generation != _loadGeneration) return;
            Logger.Warn("channels did not load", ex);
            ErrorKey = ex.Status == null ? "studio.error.offline" : "studio.error.server";
            State = ShellState.Error;
            return;
        }

        // A newer load (account switch) started while this one waited.
        if (generation != _loadGeneration) return;
        var usable = channels.Where(c => !c.IsSuspended).ToList();
        Channels = usable;
        var remembered = account.ActiveChannelId;
        var pick = usable.FirstOrDefault(c => c.Id == remembered) ?? usable.FirstOrDefault(c => c.IsMain && c.Role == ChannelRole.Owner) ?? usable.FirstOrDefault();
        SetChannel(pick);
        State = pick == null ? ShellState.NoChannel : ShellState.Ready;
    }

    private void SetChannel(StudioChannel? channel)
    {
        if (ReferenceEquals(channel, ActiveChannel)) return;
        ActiveChannel = channel;
        ChannelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncAccounts()
    {
        AccountList = _accounts.Accounts.ToList();
        var active = _accounts.Active;
        if (active?.UserId != ActiveAccount?.UserId || active?.NeedsReauth != ActiveAccount?.NeedsReauth) ActiveAccount = active;
        else ActiveAccount = active;
        OnPropertyChanged(nameof(IsSignedIn));
    }

    private void OnUi(Action action)
    {
        if (_ui == null || ReferenceEquals(SynchronizationContext.Current, _ui)) action();
        else _ui.Post(_ => action(), null);
    }
}
