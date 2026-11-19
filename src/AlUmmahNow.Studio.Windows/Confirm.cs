using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// In-app confirmation (ContentDialog), never an OS message box. Cancel is the default button, so
/// Enter and Escape both cancel and Narrator lands on Cancel first.
/// </summary>
public static class Confirm
{
    public static async Task<bool> AskAsync(XamlRoot root, FlowDirection direction, string title, string body, string confirmLabel, string cancelLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = direction,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmLabel,
            CloseButtonText = cancelLabel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>A message with one button.</summary>
    public static async Task TellAsync(XamlRoot root, FlowDirection direction, string title, string body, string closeLabel)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = direction,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = closeLabel,
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}

/// <summary>Confirmations asked by view models by i18n key: "&lt;key&gt;.heading", "&lt;key&gt;" (body) and "&lt;key&gt;.ok".</summary>
public static class ConfirmKeys
{
    public static Func<string, object[], Task<bool>> For(Func<XamlRoot?> root, AlUmmahNow.App.I18n.Localizer l) => async (key, args) =>
    {
        if (root() is not { } r) return false;
        var shown = args.Select(a => a is string s && s.StartsWith("studio.", StringComparison.Ordinal) ? (object)l[s] : a).ToArray();
        return await Confirm.AskAsync(r, l.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, l[key + ".heading"], l.Format(key, shown), l[key + ".ok"], l["common.cancel"]);
    };
}
