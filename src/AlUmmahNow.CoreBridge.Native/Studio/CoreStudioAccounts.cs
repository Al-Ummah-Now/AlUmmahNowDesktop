using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Logging;
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

    private readonly IBrowserLauncher _browser;
    private readonly string _successMessage;
    private readonly TimeSpan _signInTimeout;
    private readonly object _gate = new();
    private IReadOnlyList<StudioAccount> _accounts = Array.Empty<StudioAccount>();
    private StudioAccount? _active;

    public CoreStudioAccounts(Uri issuer, string clientId, ISecureStore store, IBrowserLauncher browser, string successMessage, TimeSpan? signInTimeout = null)
    {
        _browser = browser;
        _successMessage = successMessage;
        _signInTimeout = signInTimeout ?? TimeSpan.FromMinutes(5);
        // The registered redirect is a loopback URL; each sign-in passes its own port.
        Manager = new AuthManager(
            new AuthConfig(issuer.AbsoluteUri.TrimEnd('/'), clientId, "http://127.0.0.1" + LoopbackRedirect.CallbackPath, Scopes, null, null, null),
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_signInTimeout);
        using var loopback = new LoopbackRedirect(_successMessage);
        try
        {
            var request = Manager.BeginLoginWithRedirect(mode == SignInMode.AddAccount ? LoginPrompt.Login : LoginPrompt.Default, loopback.RedirectUri);
            _browser.Open(new Uri(request.Url));
            var redirect = await loopback.WaitAsync(timeout.Token).ConfigureAwait(false);
            var info = await Manager.CompleteLogin(redirect).ConfigureAwait(false);
            Reload();
            Raise();
            return ToAccount(info);
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
