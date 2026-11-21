using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Shell;
using Xunit;

namespace AlUmmahNow.Studio.Tests;

/// <summary>The one identity menu at the head of the sidebar: channels (name + "Channel"), then people (name + "Personal").</summary>
public sealed class IdentityMenuTests
{
    private static StudioChannel Channel(string id, string name, ChannelRole role) =>
        new(id, name, id, null, false, role, Array.Empty<string>(), false);

    private static StudioAccount Account(string id, string name, bool reauth = false) => new(id, id + "@example.org", name, null, null, reauth);

    [Theory]
    [InlineData("en", "Channel", "Channel · Editor", "Personal")]
    [InlineData("ar", "قناة", "قناة، محرّر", "شخصي")]
    public void ChannelsThenPeopleWithTheirTags(string language, string owner, string editor, string personal)
    {
        var l = new Localizer(language);
        var nur = Channel("c1", "قناة النور", ChannelRole.Owner);
        var team = Channel("c2", "Masjid Media Team", ChannelRole.Editor);
        var channels = IdentityMenu.Channels(new[] { nur, team }, nur, k => l[k], l.Separator);
        Assert.Equal(new[] { owner, editor }, channels.Select(r => r.Tag));
        Assert.True(channels[0].IsActive);
        Assert.False(channels[1].IsActive);

        var amina = Account("u1", "Amina Yusuf");
        var people = IdentityMenu.Accounts(new[] { amina, Account("u2", "Yusuf Ali") }, amina, a => a.DisplayName!, k => l[k], l.Separator);
        Assert.All(people, r => Assert.Equal(personal, r.Tag));
        Assert.Equal(IdentityKind.Account, people[0].Kind);
        Assert.True(people[0].IsActive);
    }

    [Fact]
    public void AnAccountThatNeedsSignInSaysSo()
    {
        var l = new Localizer("en");
        var rows = IdentityMenu.Accounts(new[] { Account("u1", "Amina Yusuf", reauth: true) }, null, a => a.DisplayName!, k => l[k], l.Separator);
        Assert.Equal("Sign-in needed", rows[0].Tag);
        Assert.True(rows[0].NeedsReauth);
    }

    [Fact]
    public void TheButtonShowsTheActiveChannelElseTheActivePerson()
    {
        var l = new Localizer("en");
        var amina = Account("u1", "Amina Yusuf");
        var people = IdentityMenu.Accounts(new[] { amina }, amina, a => a.DisplayName!, k => l[k], l.Separator);
        Assert.Equal("Amina Yusuf", IdentityMenu.Current(Array.Empty<IdentityRow>(), people)!.Name);

        var nur = Channel("c1", "قناة النور", ChannelRole.Owner);
        var channels = IdentityMenu.Channels(new[] { nur }, nur, k => l[k], l.Separator);
        Assert.Equal("قناة النور", IdentityMenu.Current(channels, people)!.Name);
        Assert.Null(IdentityMenu.Current(Array.Empty<IdentityRow>(), Array.Empty<IdentityRow>()));
    }

    [Fact]
    public void ScreenReadersHearNameAndTag()
    {
        var l = new Localizer("en");
        var team = Channel("c2", "Masjid Media Team", ChannelRole.Editor);
        Assert.Equal("Masjid Media Team · Channel · Editor", IdentityMenu.Channels(new[] { team }, null, k => l[k], l.Separator)[0].AccessibleName);
        var ar = new Localizer("ar");
        Assert.Equal("Masjid Media Team، قناة، محرّر", IdentityMenu.Channels(new[] { team }, null, k => ar[k], ar.Separator)[0].AccessibleName);
    }
}
