using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

public sealed class ShellViewModelTests
{
    private readonly FakeAccounts _accounts = new();
    private readonly FakeTransport _t = new();

    private void Channels(params object[] rows) =>
        _t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { success = true, data = rows });

    private static object Row(string id, bool main = false, string role = "owner", bool suspended = false) =>
        new { id, userId = "u1", name = "Channel " + id, isMain = main, role, capabilities = Array.Empty<string>(), isSuspended = suspended };

    private StudioShellViewModel Shell() => new(_accounts, new StudioApiClient(_t));

    [Fact]
    public async Task SignedOutWithoutAccounts()
    {
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.Equal(ShellState.SignedOut, shell.State);
        Assert.False(shell.IsSignedIn);
    }

    [Fact]
    public async Task SignInPicksTheMainOwnedChannel()
    {
        Channels(Row("c2", role: "editor"), Row("c1", main: true), Row("c3", suspended: true));
        var shell = Shell();
        await shell.InitializeAsync();
        await shell.SignInAsync(SignInMode.Default);
        Assert.Equal(ShellState.Ready, shell.State);
        Assert.Equal("c1", shell.ActiveChannel!.Id);
        Assert.Equal(2, shell.Channels.Count);
    }

    [Fact]
    public async Task RemembersTheChosenChannel()
    {
        Channels(Row("c1", main: true), Row("c2"));
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, "c2", false));
        _accounts.ActiveId = "u1";
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.Equal("c2", shell.ActiveChannel!.Id);
        var changed = 0;
        shell.ChannelChanged += (_, _) => changed++;
        shell.SelectChannel(shell.Channels[0]);
        Assert.Equal("c1", _accounts.LastChannel);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task NoChannelAndNeedsReauthStates()
    {
        Channels();
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.Saved.Add(new StudioAccount("u2", null, "B", null, null, true));
        _accounts.ActiveId = "u1";
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.Equal(ShellState.NoChannel, shell.State);
        await shell.SwitchAccountAsync("u2");
        Assert.Equal(ShellState.NeedsReauth, shell.State);
    }

    [Fact]
    public async Task FailedSignInKeepsTheStateAndReportsIt()
    {
        _accounts.SignInError = new AuthFailedException("closed");
        var shell = Shell();
        await shell.InitializeAsync();
        await shell.SignInAsync(SignInMode.Default);
        Assert.Equal(ShellState.SignedOut, shell.State);
        Assert.Equal("auth.failed", shell.ErrorKey);
    }

    [Fact]
    public async Task ExpiredSessionWhileLoadingAsksToSignInAgain()
    {
        _t.Responses["get_ChannelAccessList"] = _ => throw new AuthRequiredException("401");
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.ActiveId = "u1";
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.Equal(ShellState.NeedsReauth, shell.State);
    }

    [Fact]
    public async Task ServerErrorIsReportedAndRetryWorks()
    {
        var fail = true;
        _t.Responses["get_ChannelAccessList"] = _ => fail ? throw new StudioApiException("down", 503) : FakeTransport.Json(new { data = new[] { Row("c1") } });
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.ActiveId = "u1";
        var shell = Shell();
        await shell.InitializeAsync();
        Assert.Equal(ShellState.Error, shell.State);
        Assert.Equal("studio.error.server", shell.ErrorKey);
        fail = false;
        await shell.RetryAsync();
        Assert.Equal(ShellState.Ready, shell.State);
    }

    [Fact]
    public async Task SignOutMovesToTheNextAccount()
    {
        Channels(Row("c1"));
        _accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        _accounts.Saved.Add(new StudioAccount("u2", null, "B", null, null, false));
        _accounts.ActiveId = "u1";
        var shell = Shell();
        await shell.InitializeAsync();
        await shell.SignOutAsync("u1");
        Assert.Equal(new[] { "u1" }, _accounts.SignedOut);
        Assert.Equal("u2", shell.ActiveAccount!.UserId);
        Assert.Equal(ShellState.Ready, shell.State);
    }
}
