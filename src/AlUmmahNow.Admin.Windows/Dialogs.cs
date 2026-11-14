using AlUmmahNow.Admin.Review;
using AlUmmahNow.App.I18n;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// In-app dialogs (ContentDialog, never an OS message box), in the window's language and
/// direction. Cancel is the default button, so Enter and Escape cancel and Narrator starts there.
/// </summary>
internal static class Dialogs
{
    private static bool _open;

    public static async Task<bool> ConfirmAsync(XamlRoot root, FlowDirection direction, string title, string body, string confirm, string cancel)
    {
        if (_open) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = direction,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>A message with one button (passkey results).</summary>
    public static async Task MessageAsync(XamlRoot root, FlowDirection direction, string title, string body, string close)
    {
        if (_open) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = direction,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
    }

    /// <summary>Reason (required for reject and request changes, optional for escalate) and a note to the creator.</summary>
    public static async Task<(string? Code, string? Note)?> ReasonAsync(XamlRoot root, FlowDirection direction, Localizer l, string title, bool required, string confirm)
    {
        if (_open) return null;
        var reasons = new ComboBox { Header = l["admin.reason.title"], PlaceholderText = l["admin.reason.pick"], HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(reasons, "ReasonPicker");
        AutomationProperties.SetName(reasons, l["admin.reason.title"]);
        foreach (var reason in ReviewReasons.Codes) reasons.Items.Add(new ComboBoxItem { Content = l["admin.reason." + reason], Tag = reason });
        var note = new TextBox { Header = l["admin.reason.note"], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1000, MinHeight = 80 };
        AutomationProperties.SetAutomationId(note, "ReasonNote");
        var error = new TextBlock { Text = l["admin.reason.required"], Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(reasons);
        panel.Children.Add(note);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            FlowDirection = direction,
            Title = title,
            Content = panel,
            PrimaryButtonText = confirm,
            CloseButtonText = l["admin.cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (required && reasons.SelectedItem == null)
            {
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
                reasons.Focus(FocusState.Keyboard);
            }
        };
        dialog.Opened += (_, _) => reasons.Focus(FocusState.Programmatic);
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        var code = (reasons.SelectedItem as ComboBoxItem)?.Tag as string;
        return (code, string.IsNullOrWhiteSpace(note.Text) ? null : note.Text);
    }

    public static async Task ShortcutsAsync(XamlRoot root, FlowDirection direction, Localizer l)
    {
        if (_open) return;
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = 0;
        foreach (var (key, label) in ReviewShortcuts.Help)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = l[key] };
            var keys = new TextBlock { Text = label, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetName(keys, l[key] + ": " + label);
            Grid.SetRow(name, row);
            Grid.SetRow(keys, row);
            Grid.SetColumn(keys, 1);
            grid.Children.Add(name);
            grid.Children.Add(keys);
            row++;
        }

        var dialog = new ContentDialog { XamlRoot = root, FlowDirection = direction, Title = l["admin.keys.title"], Content = grid, CloseButtonText = l["admin.keys.close"], DefaultButton = ContentDialogButton.Close };
        await ShowAsync(dialog);
    }

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}
