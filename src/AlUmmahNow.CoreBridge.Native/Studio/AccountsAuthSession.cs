using AlUmmahNow.Core;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.CoreBridge.Native.Studio;

/// <summary>Presents the active Studio account as an <see cref="IAuthSession"/> for code that needs a bearer token.</summary>
public sealed class AccountsAuthSession : IAuthSession
{
    private readonly CoreStudioAccounts _accounts;

    public AccountsAuthSession(CoreStudioAccounts accounts)
    {
        _accounts = accounts;
        _accounts.Changed += (s, e) => AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? AccountChanged;

    public AuthAccount? Account => _accounts.Active is { } a ? new AuthAccount(a.UserId, a.Email) : null;

    public void Restore() => _accounts.Reload();

    public Task SignInAsync(CancellationToken cancellationToken = default) => _accounts.SignInAsync(SignInMode.Default, cancellationToken);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _accounts.Manager.AccessToken().WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new AuthRequiredException("Not signed in");
        }
        catch (AunException ex)
        {
            throw CoreApiTransport.Map(ex, "access token");
        }
    }

    public void SignOut()
    {
        if (_accounts.Active is { } a) _ = _accounts.SignOutAsync(a.UserId);
    }
}
