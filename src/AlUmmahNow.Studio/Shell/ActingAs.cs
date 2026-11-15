using System.Globalization;
using AlUmmahNow.CoreBridge.Studio;

namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// Wording for the "Acting as" picker, the YouTube/Facebook pattern the website uses: each option
/// shows a logo, a name and a small tag saying what it is. The Studio always works in a channel
/// (uploads need one), so every option is a channel tagged "Channel"; a personal identity would be
/// tagged "Personal". Names only: never an @handle.
/// </summary>
public static class ActingAs
{
    /// <summary>Picker heading: "Acting as" / «تتصرّف باسم».</summary>
    public const string HeadingKey = "studio.shell.actingAs";

    /// <summary>Accessible name of the picker button: "Acting as {name}".</summary>
    public const string ButtonNameKey = "studio.shell.actingAsMenu";

    /// <summary>Small tag on a channel option: "Channel" / «قناة».</summary>
    public const string ChannelTagKey = "studio.shell.tag.channel";

    public static string RoleKey(ChannelRole role) => "studio.shell.channelRole." + role.ToString().ToLowerInvariant();

    /// <summary>The tag under a channel's name: "Channel", plus the role when the user isn't the owner.</summary>
    public static string ChannelTag(StudioChannel channel, Func<string, string> text) =>
        channel.Role == ChannelRole.Owner ? text(ChannelTagKey) : text(ChannelTagKey) + " · " + text(RoleKey(channel.Role));

    /// <summary>One line for a channel option (screen readers, plain-text lists): "Name · Channel".</summary>
    public static string OptionText(StudioChannel channel, Func<string, string> text) => channel.Name + " · " + ChannelTag(channel, text);

    /// <summary>The grapheme clusters of a short string (e.g. initials "MT" → "M", "T").</summary>
    public static IEnumerable<string> Graphemes(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) yield return (string)e.Current;
    }
}
