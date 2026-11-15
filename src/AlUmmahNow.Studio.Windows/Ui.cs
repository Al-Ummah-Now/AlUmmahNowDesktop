using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Small builders shared by the Studio pages. Colours are theme brushes, so light, dark and high contrast apply.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
        return t;
    }

    public static TextBlock Text(string text, bool dim = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (dim) t.Foreground = Brush("TextFillColorSecondaryBrush");
        return t;
    }

    /// <summary>A raised panel: card fill, card stroke, rounded corners.</summary>
    public static StackPanel Card(double spacing = 6)
    {
        return new StackPanel
        {
            Spacing = spacing,
            Padding = new Thickness(16, 12, 16, 12),
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
    }

    public static Button Button(string text, Action click, bool accent = false, string? name = null)
    {
        var b = new Button { Content = text };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (name != null) AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A message bar, collapsed until it has something to say (a closed bar would still take a stack slot).</summary>
    public static InfoBar Bar(InfoBarSeverity severity) => new() { Severity = severity, IsClosable = false, IsOpen = false, Visibility = Visibility.Collapsed };

    public static void Show(InfoBar bar, string? message)
    {
        bar.Message = message ?? "";
        bar.IsOpen = !string.IsNullOrEmpty(message);
        bar.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sets a status line and collapses it when empty, so empty lines leave no gap.</summary>
    public static void Status(TextBlock text, string? value)
    {
        text.Text = value ?? "";
        text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static StackPanel Row(double spacing = 8) => new() { Orientation = Orientation.Horizontal, Spacing = spacing };
}
