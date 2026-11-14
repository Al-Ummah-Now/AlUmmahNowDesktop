using AlUmmahNow.CoreBridge.Passkeys;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class PasskeyShellTests
{
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();

    private StudioShellViewModel Shell()
    {
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new
        {
            success = true,
            data = new[] { new { id = "c1", userId = "u1", name = "Channel", isMain = true, role = "owner", capabilities = Array.Empty<string>(), isSuspended = false } },
        });
        return new StudioShellViewModel(_accounts, new StudioApiClient(_t));
    }

    [Fact]
    public async Task Passkey_sign_in_loads_the_channel()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.True(shell.PasskeysAvailable);
        var outcome = await shell.SignInWithPasskeyAsync();
        Assert.Same(PasskeyOutcome.Done, outcome);
        Assert.Equal(1, _accounts.PasskeySignIns);
        Assert.Equal(ShellState.Ready, shell.State);
        Assert.False(shell.PasskeyBusy);
    }

    [Fact]
    public async Task Closing_the_sheet_changes_nothing_and_shows_nothing()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        _accounts.PasskeyError = new PasskeyException(PasskeyError.Cancelled, "closed");
        var outcome = await shell.SignInWithPasskeyAsync();
        Assert.False(outcome.ShowsDialog);
        Assert.Equal(ShellState.SignedOut, shell.State);
        Assert.Null(shell.ErrorKey);
        Assert.False(shell.PasskeyBusy);
    }

    [Fact]
    public async Task No_passkey_offers_the_other_way()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        _accounts.PasskeyError = new PasskeyException(PasskeyError.NoPasskey, "none");
        var outcome = await shell.SignInWithPasskeyAsync();
        Assert.Equal("passkey.error.none", outcome.MessageKey);
        Assert.True(outcome.OfferOtherWay);
        Assert.Equal(ShellState.SignedOut, shell.State);
    }

    [Fact]
    public async Task Hidden_where_the_platform_has_no_passkeys()
    {
        _accounts.PasskeysAvailable = false;
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.False(shell.PasskeysAvailable);
    }

    [Fact]
    public async Task Add_a_passkey_needs_an_account()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.False((await shell.AddPasskeyAsync(null)).ShowsDialog);
        Assert.Equal(0, _accounts.PasskeysAdded);

        await shell.SignInAsync(SignInMode.Default);
        Assert.Same(PasskeyOutcome.Done, await shell.AddPasskeyAsync(null));
        Assert.Equal(1, _accounts.PasskeysAdded);
    }

    [Fact]
    public async Task Add_a_passkey_errors_become_one_dialog()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        await shell.SignInAsync(SignInMode.Default);
        _accounts.PasskeyError = new PasskeyException(PasskeyError.AlreadyRegistered, "exists");
        var outcome = await shell.AddPasskeyAsync(null);
        Assert.Equal("passkey.error.exists", outcome.MessageKey);
        Assert.False(outcome.OfferOtherWay);
        Assert.Equal(ShellState.Ready, shell.State);
    }
}
