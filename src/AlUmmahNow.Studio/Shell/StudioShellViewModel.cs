using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Passkeys;
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
    private readonly IFeaturesApi? _featuresApi;
    private readonly IProfileApi? _profiles;
    private readonly string? _language;
    private readonly Dictionary<string, string> _names;
    private readonly Action<IReadOnlyDictionary<string, string>>? _saveNames;
    private readonly Func<DateTimeOffset> _now;
    private readonly SynchronizationContext? _ui;
    private int _loadGeneration;
    private int _featuresGeneration;
    private DateTimeOffset _featuresAt = DateTimeOffset.MinValue;
    private string? _featuresKey;

    [ObservableProperty] private ShellState _state = ShellState.SignedOut;
    [ObservableProperty] private IReadOnlyList<StudioAccount> _accountList = Array.Empty<StudioAccount>();
    [ObservableProperty] private StudioAccount? _activeAccount;
    [ObservableProperty] private IReadOnlyList<StudioChannel> _channels = Array.Empty<StudioChannel>();
    [ObservableProperty] private StudioChannel? _activeChannel;

    /// <summary>An i18n key for the last failure, or null.</summary>
    [ObservableProperty] private string? _errorKey;

    /// <summary>The Windows security sheet is open: passkey buttons are disabled.</summary>
    [ObservableProperty] private bool _passkeyBusy;

    /// <summary>
    /// GET /api/me/features for the active account and channel. <see cref="UserFeatures.None"/> until the first
    /// answer: gated pages stay hidden. A failed refresh keeps the last answer for the same account and channel.
    /// </summary>
    [ObservableProperty] private UserFeatures _features = UserFeatures.None;

    /// <summary>Foreground refreshes closer together than this reuse the last answer.</summary>
    public static readonly TimeSpan FeaturesFreshFor = TimeSpan.FromSeconds(60);

    /// <param name="profiles">GET /api/profile, for the account's resolved display name.</param>
    /// <param name="knownNames">Names read earlier (settings), by user id.</param>
    /// <param name="saveNames">Stores <paramref name="knownNames"/> after a change.</param>
    public StudioShellViewModel(IStudioAccounts accounts, IStudioApi api, IFeaturesApi? features = null, Func<DateTimeOffset>? now = null,
        IProfileApi? profiles = null, string? language = null, IReadOnlyDictionary<string, string>? knownNames = null, Action<IReadOnlyDictionary<string, string>>? saveNames = null)
    {
        _accounts = accounts;
        _api = api;
        _featuresApi = features;
        _profiles = profiles;
        _language = language;
        _names = knownNames == null ? new Dictionary<string, string>() : new Dictionary<string, string>(knownNames.ToDictionary(kv => kv.Key, kv => kv.Value));
        _saveNames = saveNames;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ui = SynchronizationContext.Current;
        _accounts.Changed += (_, _) => OnUi(SyncAccounts);
    }

    public bool IsSignedIn => ActiveAccount != null;

    /// <summary>
    /// The name to show for <paramref name="account"/> (owner rule): its profile's display name, else first + last,
    /// else the email. Never an @handle.
    /// </summary>
    public string NameOf(StudioAccount account) =>
        AccountName.Label(_names.TryGetValue(account.UserId, out var name) ? name : account.DisplayName, account.Email, account.UserId);

    /// <summary><see cref="NameOf"/> for the active account, or null when signed out.</summary>
    public string? ActiveAccountName => ActiveAccount is { } a ? NameOf(a) : null;

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

    /// <summary>Show "Sign in with a passkey" and "Add a passkey" (the platform has a passkey API).</summary>
    public bool PasskeysAvailable => _accounts.PasskeysAvailable;

    /// <summary>Passkey sign-in, no browser. The window shows the outcome's dialog, if any.</summary>
    public async Task<PasskeyOutcome> SignInWithPasskeyAsync(CancellationToken cancellationToken = default)
    {
        if (PasskeyBusy || State == ShellState.SigningIn) return PasskeyOutcome.Silent;
        PasskeyBusy = true;
        ErrorKey = null;
        try
        {
            await _accounts.SignInWithPasskeyAsync(cancellationToken);
            Logger.Info("signed in with a passkey");
        }
        catch (Exception ex) when (ex is PasskeyException || ex is OperationCanceledException)
        {
            if (ex is PasskeyException { Error: not PasskeyError.Cancelled }) Logger.Warn("passkey sign-in failed", ex);
            return PasskeyOutcome.ForError(ex, signIn: true);
        }
        finally
        {
            PasskeyBusy = false;
        }

        SyncAccounts();
        await LoadChannelsAsync();
        return PasskeyOutcome.Done;
    }

    /// <summary>"Add a passkey" for the active account; <paramref name="confirmSignInAgain"/> asks before a step-up sign-in.</summary>
    public async Task<PasskeyOutcome> AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain, CancellationToken cancellationToken = default)
    {
        if (PasskeyBusy || ActiveAccount == null) return PasskeyOutcome.Silent;
        PasskeyBusy = true;
        try
        {
            await _accounts.AddPasskeyAsync(confirmSignInAgain, cancellationToken);
            Logger.Info("passkey added");
            return PasskeyOutcome.Done;
        }
        catch (Exception ex) when (ex is PasskeyException || ex is OperationCanceledException)
        {
            if (ex is PasskeyException { Error: not PasskeyError.Cancelled }) Logger.Warn("adding a passkey failed", ex);
            return PasskeyOutcome.ForError(ex, signIn: false);
        }
        finally
        {
            PasskeyBusy = false;
        }
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
        if (_names.Remove(userId)) _saveNames?.Invoke(_names);
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
        _ = RefreshFeaturesAsync();
    }

    public Task RetryAsync() => LoadChannelsAsync();

    /// <summary>
    /// Re-reads <see cref="Features"/>. The window calls it when it comes to the foreground (throttled to
    /// <see cref="FeaturesFreshFor"/>); pages call it with <paramref name="force"/> after a 404 <c>feature_off</c> or 402.
    /// </summary>
    public async Task RefreshFeaturesAsync(bool force = false)
    {
        if (_featuresApi == null) return;
        var account = ActiveAccount;
        if (account == null || account.NeedsReauth)
        {
            Interlocked.Increment(ref _featuresGeneration);
            _featuresKey = null;
            Features = UserFeatures.None;
            return;
        }

        var channelId = ActiveChannel?.Id;
        var key = account.UserId + "\n" + channelId;
        if (_featuresKey == null || !_featuresKey.StartsWith(account.UserId + "\n", StringComparison.Ordinal))
        {
            // Another account's answer never stands in for this one.
            Features = UserFeatures.None;
        }

        if (key != _featuresKey)
        {
            // A channel switch keeps the account's last answer on screen until this one arrives (no flicker).
            _featuresKey = key;
            _featuresAt = DateTimeOffset.MinValue;
        }
        else if (!force && _now() - _featuresAt < FeaturesFreshFor)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _featuresGeneration);
        try
        {
            var features = await _featuresApi.MyFeaturesAsync(channelId);
            if (generation != _featuresGeneration) return;
            _featuresAt = _now();
            if (!features.SameAs(Features)) Features = features;
        }
        catch (AuthRequiredException)
        {
            if (generation != _featuresGeneration) return;
            ReportAuthRequired();
        }
        catch (Exception ex) when (ex is StudioApiException || ex is RateLimitedException)
        {
            // Keep the last known answer (or nothing, so gated pages stay hidden).
            Logger.Warn("features did not load", ex);
        }
    }

    /// <summary>Called when any page gets <see cref="AuthRequiredException"/>.</summary>
    public void ReportAuthRequired()
    {
        _accounts.Reload();
        SyncAccounts();
        State = ShellState.NeedsReauth;
        Interlocked.Increment(ref _featuresGeneration);
        _featuresKey = null;
        Features = UserFeatures.None;
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
            await RefreshFeaturesAsync();
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
        catch (Exception ex) when (ex is RateLimitedException || ex is OperationCanceledException)
        {
            // Without this the window stayed on "Loading…" for good (an endless spinner).
            if (generation != _loadGeneration) return;
            Logger.Warn("channels did not load", ex);
            ErrorKey = "studio.error.server";
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
        await RefreshFeaturesAsync(force: true);
        await RefreshNameAsync(account.UserId);
    }

    /// <summary>Reads the active account's resolved name. A failure keeps the name already known.</summary>
    private async Task RefreshNameAsync(string userId)
    {
        // The transport sends as the active account: only ask while that is still this account.
        if (_profiles == null || ActiveAccount?.UserId != userId) return;
        string? name;
        try
        {
            name = await _profiles.MyNameAsync(_language);
        }
        catch (Exception ex) when (ex is StudioApiException || ex is RateLimitedException || ex is AuthRequiredException)
        {
            Logger.Warn("profile name did not load", ex);
            return;
        }

        // Another account became active meanwhile: this answer is for the old token's account only if it still is.
        if (ActiveAccount?.UserId != userId) return;
        var changed = name == null ? _names.Remove(userId) : !_names.TryGetValue(userId, out var old) || old != name;
        if (name != null) _names[userId] = name;
        if (!changed) return;
        _saveNames?.Invoke(_names);
        OnPropertyChanged(nameof(ActiveAccountName));
        OnPropertyChanged(nameof(AccountList));
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
