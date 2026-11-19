using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// In-app confirmation (ContentDialog), never an OS message box, in Lantern Glass. For a destructive
/// action Cancel is the default button, so Enter and Escape both cancel and Narrator lands on Cancel
/// first, and the action is red; otherwise the action is the gold default.
/// </summary>
public static class Confirm
{
    public static async Task<bool> AskAsync(XamlRoot root, FlowDirection direction, string title, string body, string confirmLabel, string cancelLabel, bool destructive = true)
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
        return await LanternGlass.Dialog(dialog, destructive).ShowAsync() == ContentDialogResult.Primary;
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
        await LanternGlass.Dialog(dialog, destructive: false).ShowAsync();
    }
}
