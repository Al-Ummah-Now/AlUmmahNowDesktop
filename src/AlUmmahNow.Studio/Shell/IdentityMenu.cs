using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Shell;

/// <summary>What a row of the identity menu stands for.</summary>
public enum IdentityKind
{
    /// <summary>A channel the active account owns or helps run: picking it makes it the acting identity.</summary>
    Channel,

    /// <summary>A signed-in person (account): picking it switches to that account.</summary>
    Account,
}

/// <summary>One row of the identity menu: logo or initials, name, and the small tag under the name.</summary>
/// <param name="AccessibleName">One line for screen readers: name, the language's separator, tag.</param>
public sealed record IdentityRow(IdentityKind Kind, string Id, string Name, string Tag, string? Initials, string? AvatarUrl, bool IsActive, bool NeedsReauth, string AccessibleName);

/// <summary>
/// The one identity menu at the head of the Studio sidebar (owner rule: the acting identity is chosen
/// only there). It replaces the separate Channel picker and Account menu: the channels you can act as
/// (name + "Channel", with your role when you aren't the owner), then the signed-in people (name +
/// "Personal"), then Add another account and Sign out. Names only, never an @handle.
/// </summary>
public static class IdentityMenu
{
    public const string ChannelsHeadingKey = ActingAs.HeadingKey;
    public const string AccountsHeadingKey = "studio.shell.accounts";
    public const string PersonalTagKey = "studio.shell.tag.personal";

    /// <param name="separator">Localizer.Separator: a middle dot in English, a bullet in Arabic.</param>
    public static IReadOnlyList<IdentityRow> Channels(IReadOnlyList<StudioChannel> channels, StudioChannel? active, Func<string, string> text, string separator) =>
        channels.Select(c =>
        {
            var tag = ActingAs.ChannelTag(c, text, separator);
            return new IdentityRow(IdentityKind.Channel, c.Id, c.Name, tag, Shell.Initials.For(c.Name), c.AvatarUrl, ReferenceEquals(c, active), false, c.Name + separator + tag);
        }).ToList();

    public static IReadOnlyList<IdentityRow> Accounts(IReadOnlyList<StudioAccount> accounts, StudioAccount? active, Func<StudioAccount, string> nameOf, Func<string, string> text, string separator) =>
        accounts.Select(a =>
        {
            var name = nameOf(a);
            var tag = a.NeedsReauth ? text("studio.shell.needsReauth") : text(PersonalTagKey);
            return new IdentityRow(IdentityKind.Account, a.UserId, name, tag, Shell.Initials.For(name), a.AvatarUrl, a.UserId == active?.UserId, a.NeedsReauth, name + separator + tag);
        }).ToList();

    /// <summary>
    /// The row the sidebar button shows: the active channel, or the active person while no channel is
    /// picked (signed in, channels still loading). Null when nobody is signed in.
    /// </summary>
    public static IdentityRow? Current(IReadOnlyList<IdentityRow> channels, IReadOnlyList<IdentityRow> accounts) =>
        channels.FirstOrDefault(r => r.IsActive) ?? accounts.FirstOrDefault(r => r.IsActive);
}
