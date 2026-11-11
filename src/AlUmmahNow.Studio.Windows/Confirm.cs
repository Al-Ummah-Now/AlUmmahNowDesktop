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
}
