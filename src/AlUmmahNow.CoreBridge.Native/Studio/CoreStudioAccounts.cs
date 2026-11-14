using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary>
/// <see cref="IStudioAccounts"/> on aun-auth's AuthManager: tokens in the OS secure store,
/// browser sign-in on a loopback redirect, sign-out that revokes the refresh token.
/// </summary>
public sealed class CoreStudioAccounts : IStudioAccounts, IDisposable
{
    public static readonly string[] Scopes = { "openid", "profile", "email", "offline_access" };

    private static readonly Logger Logger = Log.For("auth");

    /// <summary>The client's registered loopback redirect (no port): what a passkey code is exchanged with.</summary>
    public const string PasskeyRedirect = "http://127.0.0.1" + LoopbackRedirect.CallbackPath;

    private readonly IBrowserLauncher _browser;
    private readonly PasskeyFlows? _passkeys;
    private readonly string _successMessage;
    private readonly TimeSpan _signInTimeout;
    private readonly object _gate = new();
    private IReadOnlyList<StudioAccount> _accounts = Array.Empty<StudioAccount>();
    private StudioAccount? _active;

    public CoreStudioAccounts(Uri issuer, string clientId, ISecureStore store, IBrowserLauncher browser, string successMessage, TimeSpan? signInTimeout = null, PasskeyFlows? passkeys = null)
    {
        _browser = browser;
        _passkeys = passkeys;
        _successMessage = successMessage;
        _signInTimeout = signInTimeout ?? TimeSpan.FromMinutes(5);
        // The registered redirect is a loopback URL; each sign-in passes its own port.
        Manager = new AuthManager(
            new AuthConfig(issuer.AbsoluteUri.TrimEnd('/'), clientId, PasskeyRedirect, Scopes, null, null, null),
            new SecureStorageAdapter(store));
        Manager.AddListener(new Listener(this));
    }

    /// <summary>The core manager, for API clients bound to an account.</summary>
    public AuthManager Manager { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<StudioAccount> Accounts
    {
        get { lock (_gate) return _accounts; }
    }

    public StudioAccount? Active
    {
        get { lock (_gate) return _active; }
    }

    public void Reload()
    {
        List<StudioAccount> accounts;
        StudioAccount? active;
        try
        {
            accounts = Manager.Accounts().Select(ToAccount).ToList();
            active = Manager.ActiveAccount() is { } a ? ToAccount(a) : null;
        }
        catch (AunException ex)
        {
            // No keyring (headless session): behave as signed out rather than crash.
            Logger.Warn("saved accounts unreadable", ex);
            accounts = new List<StudioAccount>();
            active = null;
        }

        lock (_gate)
        {
            _accounts = accounts;
            _active = active;
        }
    }

    public async Task<StudioAccount> SignInAsync(SignInMode mode, CancellationToken cancellationToken = default)
    {
        var info = await BrowserSignInAsync(mode == SignInMode.AddAccount ? LoginPrompt.Login : LoginPrompt.Default, cancellationToken).ConfigureAwait(false);
        Reload();
        Raise();
        return ToAccount(info);
    }

    public bool PasskeysAvailable => _passkeys?.IsAvailable == true;

    public async Task<StudioAccount> SignInWithPasskeyAsync(CancellationToken cancellationToken = default)
    {
        if (_passkeys == null) throw new PasskeyException(PasskeyError.Unavailable, "No passkey support in this build");
        LoginRequest request;
        try
        {
            // Core keeps the PKCE verifier and state; the passkey's code completes the same login.
            request = Manager.BeginLoginWithRedirect(LoginPrompt.Default, PasskeyRedirect);
        }
        catch (AunException ex)
        {
            throw new PasskeyException(PasskeyError.Failed, "Could not start sign-in: " + ex.GetType().Name, ex);
        }

        var redirect = await _passkeys.SignInAsync(request.Url, cancellationToken).ConfigureAwait(false);
        AccountInfo info;
        try
        {
            info = await Manager.CompleteLogin(redirect).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            throw new PasskeyException(ex is AunException.Network ? PasskeyError.Offline : PasskeyError.Failed, "Token exchange failed: " + ex.GetType().Name, ex);
        }

        Logger.Info("signed in with a passkey");
        Reload();
        Raise();
        return ToAccount(info);
    }

    public async Task<SavedPasskey> AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken cancellationToken = default)
    {
        if (_passkeys == null) throw new PasskeyException(PasskeyError.Unavailable, "No passkey support in this build");
        var userId = Active?.UserId ?? throw new PasskeyException(PasskeyError.Failed, "No active account");
        var saved = await _passkeys.AddAsync(
            async ct =>
            {
                try
                {
                    return await Manager.AccessToken().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (AunException ex)
                {
                    Logger.Warn("no access token for passkey registration: " + ex.GetType().Name);
                    return null;
                }
            },
            async ct => (confirmSignInAgain == null || await confirmSignInAgain(ct).ConfigureAwait(false)) && await ReSignInAsync(userId, ct).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Logger.Info("passkey added");
        return saved;
    }

    /// <summary>A fresh browser sign-in of the same account (step-up). False when cancelled or another account signed in.</summary>
    private async Task<bool> ReSignInAsync(string userId, CancellationToken cancellationToken)
    {
        var known = Accounts.Select(a => a.UserId).ToHashSet();
        AccountInfo info;
        try
        {
            info = await BrowserSignInAsync(LoginPrompt.Login, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthFailedException ex)
        {
            Logger.Warn("re-sign-in for passkey did not finish", ex);
            return false;
        }

        if (info.UserId != userId)
        {
            Logger.Warn("re-sign-in chose another account; switching back");
            if (!known.Contains(info.UserId)) await Manager.SignOutAndRevoke(info.UserId).ConfigureAwait(false);
            Manager.SwitchAccount(userId);
            Reload();
            Raise();
            return false;
        }

        Reload();
        Raise();
        return true;
    }

    private async Task<AccountInfo> BrowserSignInAsync(LoginPrompt prompt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_signInTimeout);
        using var loopback = new LoopbackRedirect(_successMessage);
        try
        {
            var request = Manager.BeginLoginWithRedirect(prompt, loopback.RedirectUri);
            _browser.Open(new Uri(request.Url));
            var redirect = await loopback.WaitAsync(timeout.Token).ConfigureAwait(false);
            return await Manager.CompleteLogin(redirect).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthFailedException("Sign-in timed out", ex);
        }
        catch (AunException.Cancelled ex)
        {
            throw new AuthFailedException("Sign-in was cancelled in the browser", ex);
        }
        catch (AunException ex)
        {
            throw new AuthFailedException("Sign-in failed: " + ex.GetType().Name, ex);
        }
    }

    public void Switch(string userId)
    {
        Manager.SwitchAccount(userId);
        Reload();
        Raise();
    }

    public async Task SignOutAsync(string userId)
    {
        await Manager.SignOutAndRevoke(userId).ConfigureAwait(false);
        Reload();
        Raise();
    }

    public void SetActiveChannel(string? channelId)
    {
        Manager.SetActiveChannel(channelId);
        Reload();
    }

    public void Dispose() => Manager.Dispose();

    internal static StudioAccount ToAccount(AccountInfo a) =>
        new(a.UserId, a.Email, a.DisplayName, a.AvatarUrl, a.ActiveChannelId, a.NeedsReauth);

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Listener : AuthListener
    {
        private readonly CoreStudioAccounts _owner;

        public Listener(CoreStudioAccounts owner) => _owner = owner;

        public void OnEvent(AuthEvent @event)
        {
            if (@event is AuthEvent.ReauthRequired) Logger.Warn("an account must sign in again");
            try
            {
                _owner.Reload();
            }
            catch (AunException ex)
            {
                Logger.Warn("accounts could not be re-read", ex);
            }

            _owner.Raise();
        }
    }
}
