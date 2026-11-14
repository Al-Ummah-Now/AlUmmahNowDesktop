using AlUmmahNow.Admin.Auth;
using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.CoreBridge.Passkeys;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// Admin sign-in on aun-auth's AuthManager, like Desktop Studio: system browser, PKCE, tokens in
/// DPAPI. The code comes back on alummahnow-admin://auth/callback through the app's protocol
/// registration (Program redirects it to the running instance). One account at a time.
/// </summary>
internal sealed class CoreAdminAuth : IAdminAuth, IDisposable
{
    private static readonly string[] Scopes = { "openid", "profile", "email", "offline_access" };
    private static readonly Logger Logger = Log.For("auth");

    private readonly IBrowserLauncher _browser;
    private readonly PasskeyFlows? _passkeys;
    private readonly object _gate = new();
    private TaskCompletionSource<string>? _pending;

    public CoreAdminAuth(Uri issuer, string clientId, ISecureStore store, IBrowserLauncher browser, PasskeyFlows? passkeys = null)
    {
        _browser = browser;
        _passkeys = passkeys;
        Manager = new AuthManager(new AuthConfig(issuer.AbsoluteUri.TrimEnd('/'), clientId, AdminRedirect.Uri, Scopes, null, null, null), new SecureStorageAdapter(store));
    }

    public AuthManager Manager { get; }

    private AccountInfo? Active
    {
        get
        {
            try
            {
                return Manager.ActiveAccount();
            }
            catch (AunException ex)
            {
                Logger.Warn("saved account unreadable", ex);
                return null;
            }
        }
    }

    public bool IsSignedIn => Active is { NeedsReauth: false };

    public string? UserId => Active?.UserId;

    public string? DisplayName => Active is { } a ? a.DisplayName ?? a.Email : null;

    public string? Email => Active?.Email;

    public async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        try
        {
            return await Manager.AccessToken().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            Logger.Warn("no access token: " + ex.GetType().Name);
            return null;
        }
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        await RunAsync(LoginPrompt.Default, stepUp: false, ct).ConfigureAwait(false);
    }

    public async Task<bool> StepUpAsync(CancellationToken ct = default)
    {
        var before = UserId;
        try
        {
            var info = await RunAsync(LoginPrompt.Login, stepUp: true, ct).ConfigureAwait(false);
            if (before != null && info.UserId != before)
            {
                // Someone else signed in at the step-up prompt: that is not a step-up of this reviewer.
                Logger.Warn("step-up signed in a different account; reverting");
                await Manager.SignOutAndRevoke(info.UserId).ConfigureAwait(false);
                Manager.SwitchAccount(before);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is AuthFailedException || ex is OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<AccountInfo> RunAsync(LoginPrompt prompt, bool stepUp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending?.TrySetCanceled();
            _pending = tcs;
        }

        try
        {
            var request = Manager.BeginLoginWithRedirect(prompt, AdminRedirect.Uri);
            var url = new Uri(request.Url);
            _browser.Open(stepUp ? StepUp.WithMaxAge(url) : url);
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                var redirect = await tcs.Task.ConfigureAwait(false);
                return await Manager.CompleteLogin(redirect).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AuthFailedException("Sign-in timed out", ex);
        }
        catch (AunException ex)
        {
            throw new AuthFailedException("Sign-in failed: " + ex.GetType().Name, ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, tcs)) _pending = null;
            }
        }
    }

    public bool PasskeysAvailable => _passkeys?.IsAvailable == true;

    public async Task SignInWithPasskeyAsync(CancellationToken ct = default)
    {
        if (_passkeys == null) throw new PasskeyException(PasskeyError.Unavailable, "No passkey support in this build");
        LoginRequest request;
        try
        {
            // Core keeps the PKCE verifier and state; the passkey's code completes the same login.
            request = Manager.BeginLoginWithRedirect(LoginPrompt.Default, AdminRedirect.Uri);
        }
        catch (AunException ex)
        {
            throw new PasskeyException(PasskeyError.Failed, "Could not start sign-in: " + ex.GetType().Name, ex);
        }

        var redirect = await _passkeys.SignInAsync(request.Url, ct).ConfigureAwait(false);
        try
        {
            await Manager.CompleteLogin(redirect).ConfigureAwait(false);
        }
        catch (AunException ex)
        {
            throw new PasskeyException(ex is AunException.Network ? PasskeyError.Offline : PasskeyError.Failed, "Token exchange failed: " + ex.GetType().Name, ex);
        }

        Logger.Info("signed in with a passkey");
    }

    public async Task AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken ct = default)
    {
        if (_passkeys == null) throw new PasskeyException(PasskeyError.Unavailable, "No passkey support in this build");
        await _passkeys.AddAsync(
            AccessTokenAsync,
            async c => (confirmSignInAgain == null || await confirmSignInAgain(c).ConfigureAwait(false)) && await StepUpAsync(c).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        Logger.Info("passkey added");
    }

    public bool HandleRedirect(string uri)
    {
        if (!AdminRedirect.IsCallback(uri)) return false;
        lock (_gate) return _pending?.TrySetResult(uri) ?? false;
    }

    public async Task SignOutAsync()
    {
        if (UserId is { } id) await Manager.SignOutAndRevoke(id).ConfigureAwait(false);
    }

    public void Dispose() => Manager.Dispose();
}

/// <summary>CI and screenshots: a signed-in reviewer, nothing sent anywhere.</summary>
internal sealed class DemoAdminAuth : IAdminAuth
{
    public bool IsSignedIn { get; private set; } = true;
    public string? UserId => IsSignedIn ? "demo-reviewer" : null;
    public string? DisplayName => IsSignedIn ? "Amina Reviewer" : null;
    public string? Email => IsSignedIn ? "amina@example.org" : null;
    public Task<string?> AccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>("demo");

    public Task SignInAsync(CancellationToken ct = default)
    {
        IsSignedIn = true;
        return Task.CompletedTask;
    }

    public Task<bool> StepUpAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task SignOutAsync()
    {
        IsSignedIn = false;
        return Task.CompletedTask;
    }

    public bool HandleRedirect(string uri) => false;

    public bool PasskeysAvailable => true;

    public Task SignInWithPasskeyAsync(CancellationToken ct = default) => SignInAsync(ct);

    public Task AddPasskeyAsync(Func<CancellationToken, Task<bool>>? confirmSignInAgain = null, CancellationToken ct = default) => Task.CompletedTask;
}
