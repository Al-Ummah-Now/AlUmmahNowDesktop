using System.ComponentModel;
using System.Runtime.InteropServices;
using AlUmmahNow.App.Demo;
using AlUmmahNow.App.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace AlUmmahNow.Updates.Views;

/// <summary>
/// The update bar, the screen of an unsupported version and Settings → Updates on WinUI, for the viewer and Studio (the
/// file is compiled into both). Wiki AUN-APP-UPDATES §12. The colors are theme resources of their own, so the views follow
/// Light, Dark and high contrast also while the app runs, whatever the app does with its own brushes.
/// </summary>
internal sealed class UpdateViews : IUpdateViews, IDisposable
{
    // The viewer builds its window anew on each page and language: a notice is announced once all the same.
    private static string? _announced;

    private readonly UpdateService _service;
    private readonly UpdateText _text;
    private readonly TextBlock _barText;
    private readonly Button _barDownload, _close, _forcedDownload, _sectionDownload, _checkNow;
    private readonly HyperlinkButton _barNotes, _sectionNotes;
    private readonly TextBlock _forcedBody, _forcedMessage, _version, _lastChecked, _result;
    private readonly FrameworkElement _sectionHow;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private FrameworkElement? _section;
    private string _openNotes = "";

    /// <param name="icon">The app's icon (PNG).</param>
    public UpdateViews(UpdateService service, UpdateText text, Stream icon, Action quit)
    {
        _service = service;
        _text = text;
        var direction = text.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        var bar = Load<Grid>("""
            <Grid x:Name="Root" HighContrastAdjustment="None" AutomationProperties.AutomationId="UpdateBar" Background="{ThemeResource AunUpdateBarBrush}" BorderBrush="{ThemeResource AunUpdateEdgeBrush}" BorderThickness="0,0,0,1" Padding="16,8,10,8" ColumnSpacing="10">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="Auto" />
              </Grid.ColumnDefinitions>
              <FontIcon Glyph="&#xE946;" FontSize="16" Foreground="{ThemeResource AunUpdateLinkBrush}" AutomationProperties.AccessibilityView="Raw" />
              <TextBlock x:Name="Text" Grid.Column="1" TextWrapping="Wrap" VerticalAlignment="Center" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.AutomationId="UpdateBarText" />
              <HyperlinkButton x:Name="Notes" Grid.Column="2" VerticalAlignment="Center" AutomationProperties.AutomationId="UpdateNotes" />
              <Button x:Name="Download" Grid.Column="3" VerticalAlignment="Center" Style="{StaticResource AccentButtonStyle}" AutomationProperties.AutomationId="UpdateDownload" />
              <Button x:Name="Close" Grid.Column="4" VerticalAlignment="Center" Width="34" Height="32" Padding="0" Background="Transparent" BorderThickness="0" AutomationProperties.AutomationId="UpdateClose">
                <FontIcon Glyph="&#xE711;" FontSize="12" />
              </Button>
            </Grid>
            """);
        bar.FlowDirection = direction;
        Bar = bar;
        _barText = Part<TextBlock>(bar, "Text");
        _barNotes = Part<HyperlinkButton>(bar, "Notes");
        _barDownload = Part<Button>(bar, "Download");
        _close = Part<Button>(bar, "Close");
        _barNotes.Content = text["updates.whatsNew"];
        _barDownload.Content = text["updates.download"];
        AutomationProperties.SetName(_close, text["updates.close"]);
        ToolTipService.SetToolTip(_close, text["updates.close"]);

        var forced = Load<Grid>("""
            <Grid x:Name="Root" HighContrastAdjustment="None" AutomationProperties.AutomationId="UpdateForced" Background="{ThemeResource AunUpdatePageBrush}">
              <ScrollViewer HorizontalScrollMode="Disabled" VerticalScrollBarVisibility="Auto">
                <Border Width="600" Margin="24" Padding="28" CornerRadius="14" BorderThickness="1" HorizontalAlignment="Center" VerticalAlignment="Center"
                        Background="{ThemeResource AunUpdateSurfaceBrush}" BorderBrush="{ThemeResource AunUpdateEdgeBrush}">
                  <StackPanel Spacing="14">
                    <Border x:Name="Icon" Width="96" Height="96" CornerRadius="20" HorizontalAlignment="Center" FlowDirection="LeftToRight" />
                    <TextBlock x:Name="Title" FontSize="22" FontWeight="Bold" TextWrapping="Wrap" TextAlignment="Center" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.HeadingLevel="Level1" />
                    <TextBlock x:Name="Body" TextWrapping="Wrap" TextAlignment="Center" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.AutomationId="UpdateForcedBody" />
                    <TextBlock x:Name="Message" TextWrapping="Wrap" TextAlignment="Center" Foreground="{ThemeResource AunUpdateTextBrush}" />
                    <TextBlock x:Name="How" TextWrapping="Wrap" TextAlignment="Center" Foreground="{ThemeResource AunUpdateDimBrush}" />
                    <TextBlock x:Name="Page" TextWrapping="Wrap" TextAlignment="Center" IsTextSelectionEnabled="True" IsTabStop="False" Foreground="{ThemeResource AunUpdateDimBrush}" />
                    <StackPanel Orientation="Horizontal" Spacing="10" HorizontalAlignment="Center" Margin="0,8,0,0">
                      <Button x:Name="Quit" AutomationProperties.AutomationId="UpdateQuit" />
                      <Button x:Name="Download" Style="{StaticResource AccentButtonStyle}" AutomationProperties.AutomationId="UpdateForcedDownload" />
                    </StackPanel>
                  </StackPanel>
                </Border>
              </ScrollViewer>
            </Grid>
            """);
        forced.FlowDirection = direction;
        Forced = forced;
        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 192 };
        _ = LoadAsync(image, icon);
        Part<Border>(forced, "Icon").Background = new Microsoft.UI.Xaml.Media.ImageBrush { ImageSource = image, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        Part<TextBlock>(forced, "Title").Text = text["updates.forced.title"];
        _forcedBody = Part<TextBlock>(forced, "Body");
        _forcedMessage = Part<TextBlock>(forced, "Message");
        Part<TextBlock>(forced, "How").Text = text.How;
        Part<TextBlock>(forced, "Page").Text = text.Page;
        var quitButton = Part<Button>(forced, "Quit");
        quitButton.Content = text["updates.quit"];
        _forcedDownload = Part<Button>(forced, "Download");
        _forcedDownload.Content = text["updates.download"];
        // A named button, not its version: the screen's own words say what it is for
        AutomationProperties.SetName(_forcedDownload, text["updates.download"]);

        var body = Load<StackPanel>("""
            <StackPanel x:Name="Root" HighContrastAdjustment="None" Spacing="6" AutomationProperties.AutomationId="UpdateSection">
              <TextBlock x:Name="Title" Style="{StaticResource SubtitleTextBlockStyle}" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.HeadingLevel="Level2" />
              <TextBlock x:Name="Version" TextWrapping="Wrap" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.AutomationId="UpdateVersion" />
              <TextBlock x:Name="LastChecked" TextWrapping="Wrap" Foreground="{ThemeResource AunUpdateDimBrush}" AutomationProperties.AutomationId="UpdateLastChecked" />
              <TextBlock x:Name="Result" TextWrapping="Wrap" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.AutomationId="UpdateResult" />
              <TextBlock x:Name="How" TextWrapping="Wrap" Foreground="{ThemeResource AunUpdateDimBrush}" />
            </StackPanel>
            """);
        Part<TextBlock>(body, "Title").Text = text["updates.title"];
        _version = Part<TextBlock>(body, "Version");
        _lastChecked = Part<TextBlock>(body, "LastChecked");
        _result = Part<TextBlock>(body, "Result");
        var how = Part<TextBlock>(body, "How");
        how.Text = text.How;
        _sectionHow = how;
        SectionBody = body;
        var actions = Load<StackPanel>("""
            <StackPanel x:Name="Root" HighContrastAdjustment="None" Orientation="Horizontal" Spacing="8">
              <HyperlinkButton x:Name="Notes" AutomationProperties.AutomationId="UpdateSectionNotes" />
              <Button x:Name="Download" Style="{StaticResource AccentButtonStyle}" AutomationProperties.AutomationId="UpdateSectionDownload" />
              <Button x:Name="CheckNow" AutomationProperties.AutomationId="UpdateCheckNow" />
            </StackPanel>
            """);
        _sectionNotes = Part<HyperlinkButton>(actions, "Notes");
        _sectionNotes.Content = text["updates.whatsNew"];
        _sectionDownload = Part<Button>(actions, "Download");
        _sectionDownload.Content = text["updates.download"];
        _checkNow = Part<Button>(actions, "CheckNow");
        _checkNow.Content = text["updates.checkNow"];
        SectionActions = actions;

        _close.Click += (_, _) => service.Dismiss();
        _barNotes.Click += (_, _) => _ = ShowNotesAsync();
        _sectionNotes.Click += (_, _) => _ = ShowNotesAsync();
        foreach (var b in new[] { _barDownload, _forcedDownload, _sectionDownload }) b.Click += (_, _) => service.Begin();
        _checkNow.Click += (_, _) => _ = service.CheckAsync(isAsked: true);
        quitButton.Click += (_, _) => quit();
        service.PropertyChanged += OnChanged;
        Render();
    }

    private static async Task LoadAsync(Microsoft.UI.Xaml.Media.Imaging.BitmapImage image, Stream icon)
    {
        using (icon)
        using (var source = icon.AsRandomAccessStream()) await image.SetSourceAsync(source);
    }

    /// <summary>Under the header, across the window.</summary>
    public FrameworkElement Bar { get; }

    /// <summary>In place of everything under the header; the window shows it when <see cref="IsForced"/>.</summary>
    public FrameworkElement Forced { get; }

    public FrameworkElement SectionBody { get; }
    public FrameworkElement SectionActions { get; }

    /// <summary>The card the two are in: it is there only while Settings → Updates is.</summary>
    public void UseSection(FrameworkElement card)
    {
        _section = card;
        Render();
    }

    /// <summary>A card of the views' own colors, for an app that has none.</summary>
    public FrameworkElement Card(FrameworkElement body)
    {
        var card = Load<Border>("""
            <Border x:Name="Root" Padding="18" CornerRadius="14" BorderThickness="1" Background="{ThemeResource AunUpdateSurfaceBrush}" BorderBrush="{ThemeResource AunUpdateEdgeBrush}">
            </Border>
            """);
        card.Child = body;
        return card;
    }

    public bool IsForced => _service.View.Stage == UpdateStage.Forced;

    /// <summary>The screen came or went.</summary>
    public event Action? ForcedChanged;

    public void Dispose() => _service.PropertyChanged -= OnChanged;

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        var was = Forced.Visibility;
        Render();
        if (Forced.Visibility != was) ForcedChanged?.Invoke();
    }

    private static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void Render()
    {
        var v = _service.View;
        Bar.Visibility = When(v.IsBar);
        _barText.Text = _text.Bar(v);
        // What is new in a version that is there to update to
        _barNotes.Visibility = When(v.CanDownload && _text.Notes(v).Length > 0);
        _barDownload.Visibility = When(v.CanDownload);
        _close.Visibility = When(v.CanClose);

        Forced.Visibility = When(IsForced);
        _forcedBody.Text = _text.ForcedBody(v);
        _forcedMessage.Text = _text.Message(v);
        _forcedMessage.Visibility = When(_forcedMessage.Text.Length > 0);

        var found = _service.Result == UpdateResult.Found;
        if (_section != null) _section.Visibility = When(_service.IsSectionVisible);
        _version.Text = _text.Version;
        _lastChecked.Text = _text.LastChecked;
        var told = _result.Text;
        _result.Text = _text.Result;
        _result.Visibility = When(_result.Text.Length > 0);
        _sectionHow.Visibility = When(found);
        _sectionDownload.Visibility = When(found);
        _sectionNotes.Visibility = When(found && _text.Notes(_service.Found).Length > 0);
        _checkNow.IsEnabled = _service.Result != UpdateResult.Checking;

        if (v.IsBar) Announce(v.Stage + ":" + v.Version, _barText, _barText.Text);
        else if (IsForced) Announce("forced", _forcedBody, _text["updates.forced.title"] + " " + _forcedBody.Text);
        // What Check now came to, for a person who cannot see it change
        if (_service.IsCheckAsked && _result.Text != told && _result.Text.Length > 0) Speak(_result, _result.Text);
    }

    /// <summary>
    /// Focus on the screen's button, once it is there: a button that is not loaded and laid out takes none, and the framework
    /// gives what a removed control had to the first control it finds, in its own time.
    /// </summary>
    public void FocusForced()
    {
        if (!_forcedDownload.IsLoaded)
        {
            void Loaded(object sender, RoutedEventArgs e)
            {
                _forcedDownload.Loaded -= Loaded;
                FocusForced();
            }

            _forcedDownload.Loaded += Loaded;
            return;
        }

        _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsForced || _forcedDownload.Focus(FocusState.Programmatic)) return;
            void LaidOut(object? sender, object e)
            {
                if (!IsForced || _forcedDownload.Focus(FocusState.Programmatic)) _forcedDownload.LayoutUpdated -= LaidOut;
            }

            _forcedDownload.LayoutUpdated += LaidOut;
        });
    }

    private void Announce(string key, TextBlock at, string text)
    {
        if (_announced == key) return;
        _announced = key;
        Speak(at, text);
    }

    private void Speak(TextBlock at, string text) => _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        FrameworkElementAutomationPeer.CreatePeerForElement(at)?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "AunUpdate"));

    private async Task ShowNotesAsync()
    {
        var found = _service.Found;
        var notes = _text.Notes(found);
        if (notes.Length == 0 || Bar.XamlRoot is not { } root) return;
        var dialog = Load<ContentDialog>("""
            <ContentDialog x:Name="Root" HighContrastAdjustment="None" Background="{ThemeResource AunUpdateSurfaceBrush}" BorderBrush="{ThemeResource AunUpdateEdgeBrush}" BorderThickness="1" CornerRadius="14">
              <ScrollViewer MaxHeight="320" HorizontalScrollMode="Disabled">
                <TextBlock x:Name="Text" TextWrapping="Wrap" Foreground="{ThemeResource AunUpdateTextBrush}" AutomationProperties.AutomationId="UpdateNotesText" />
              </ScrollViewer>
            </ContentDialog>
            """);
        dialog.XamlRoot = root;
        // A dialog is outside the page: it takes the theme and the direction of the app's window from it
        dialog.RequestedTheme = Bar.ActualTheme;
        dialog.FlowDirection = Bar.FlowDirection;
        dialog.Title = _text.NotesTitle(found);
        dialog.CloseButtonText = _text["common.close"];
        // Set as text: it comes from the server
        Part<TextBlock>(dialog, "Text").Text = notes;
        dialog.Opened += (_, _) => _openNotes = notes;
        dialog.Closed += (_, _) => _openNotes = "";
        try
        {
            await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another dialog is open
        }
    }

    private static T Part<T>(FrameworkElement root, string name)
        where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException("no part " + name);

    /// <summary>
    /// The markup with the buttons' colors in its root's resources. The views' own brushes are the application's: the root
    /// itself asks for some, and what is in its own resources is not there yet when it does.
    /// </summary>
    private static T Load<T>(string markup)
        where T : FrameworkElement
    {
        var colors = Colors.Value;
        if (!_hasBrushes)
        {
            _hasBrushes = true;
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(colors.Brushes.Replace("<ResourceDictionary>", "<ResourceDictionary " + Namespaces + ">", StringComparison.Ordinal)));
        }

        var type = typeof(T).Name;
        var end = markup.IndexOf('>', StringComparison.Ordinal) + 1;
        var root = markup[..end].Replace("<" + type + " ", "<" + type + " " + Namespaces + " ", StringComparison.Ordinal);
        return (T)XamlReader.Load(root + "<" + type + ".Resources>" + colors.Buttons + "</" + type + ".Resources>" + markup[end..]);
    }

    private const string Namespaces = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static bool _hasBrushes;

    private static readonly Lazy<(string Brushes, string Buttons)> Colors = new(() =>
    {
        // Fully qualified: inside AlUmmahNow.*, "Fanous" alone names the registry namespace AlUmmahNow.Fanous.
        var themes = AlUmmahNow.App.Theme.Fanous.ReadXamlColors(AlUmmahNow.App.Theme.Fanous.File("Fanous.Colors.xaml"));
        string Themed(Func<Dictionary<string, uint>, string> of, string highContrast = "") =>
            "<ResourceDictionary><ResourceDictionary.ThemeDictionaries>"
            + "<ResourceDictionary x:Key=\"Dark\">" + of(themes["Default"]) + "</ResourceDictionary><ResourceDictionary x:Key=\"Light\">" + of(themes["Light"]) + "</ResourceDictionary>"
            + highContrast + "</ResourceDictionary.ThemeDictionaries></ResourceDictionary>";
        return (Themed(Brushes, HighContrast), Themed(Buttons, HighContrastButtons));
    });

    private static string Hex(Dictionary<string, uint> f, string name) => "#" + f["Fanous" + name + "Color"].ToString("X8");

    private static string Brush(string key, string color) => $"<SolidColorBrush x:Key=\"{key}\" Color=\"{color}\" />";

    private static string Brushes(Dictionary<string, uint> f) =>
        Brush("AunUpdateSurfaceBrush", Hex(f, "Surface"))
        + $"<LinearGradientBrush x:Key=\"AunUpdateBarBrush\" StartPoint=\"0,0\" EndPoint=\"0,1\"><GradientStop Offset=\"0\" Color=\"{Mix(f, 0.16)}\" /><GradientStop Offset=\"1\" Color=\"{Mix(f, 0.06)}\" /></LinearGradientBrush>"
        + Brush("AunUpdatePageBrush", Hex(f, "Background"))
        // The edge's gold at 45 % over the surface, opaque
        + Brush("AunUpdateEdgeBrush", Mix(f, 0.45, "Surface", "PrimaryEdge"))
        + Brush("AunUpdateTextBrush", Hex(f, "TextPrimary"))
        + Brush("AunUpdateDimBrush", Hex(f, "TextSecondary"))
        + Brush("AunUpdateLinkBrush", Hex(f, "Link"));

    private static string Buttons(Dictionary<string, uint> f)
    {
        var all = Brush("AccentButtonBackground", Hex(f, "Primary")) + Brush("AccentButtonBackgroundPointerOver", Hex(f, "PrimaryHover")) + Brush("AccentButtonBackgroundPressed", Hex(f, "PrimaryPressed"));
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
            all += Brush("AccentButtonForeground" + state, Hex(f, "OnPrimary")) + Brush("AccentButtonBorderBrush" + state, Hex(f, "PrimaryEdge")) + Brush("HyperlinkButtonForeground" + state, Hex(f, "Link"));
        return all;
    }

    /// <summary>Gold over the surface, as one opaque color (the bar is over nothing it should show).</summary>
    private static string Mix(Dictionary<string, uint> f, double share, string under = "Surface", string over = "Primary")
    {
        var gold = f["Fanous" + over + "Color"];
        var surface = f["Fanous" + under + "Color"];
        uint Channel(int shift) => (uint)Math.Round(((gold >> shift) & 0xFF) * share + ((surface >> shift) & 0xFF) * (1 - share)) << shift;
        return "#" + (0xFF000000u | Channel(16) | Channel(8) | Channel(0)).ToString("X8");
    }

    /// <summary>
    /// The gold button and the links in high contrast: the system's, their text without a plate behind it. Studio's own link
    /// color answers for a name that is not here.
    /// </summary>
    private static readonly string HighContrastButtons = "<ResourceDictionary x:Key=\"HighContrast\">"
        + string.Concat(new[] { "", "PointerOver", "Pressed" }.Select(state =>
            Brush("AccentButtonBackground" + state, "{ThemeResource SystemColorHighlightColor}") + Brush("AccentButtonForeground" + state, "{ThemeResource SystemColorHighlightTextColor}")
            + Brush("HyperlinkButtonForeground" + state, "{ThemeResource SystemColorHotlightColor}")))
        + "</ResourceDictionary>";

    /// <summary>The system's colors.</summary>
    private const string HighContrast = """
        <ResourceDictionary x:Key="HighContrast">
          <SolidColorBrush x:Key="AunUpdateSurfaceBrush" Color="{ThemeResource SystemColorWindowColor}" />
          <SolidColorBrush x:Key="AunUpdateBarBrush" Color="{ThemeResource SystemColorWindowColor}" />
          <SolidColorBrush x:Key="AunUpdatePageBrush" Color="{ThemeResource SystemColorWindowColor}" />
          <SolidColorBrush x:Key="AunUpdateEdgeBrush" Color="{ThemeResource SystemColorWindowTextColor}" />
          <SolidColorBrush x:Key="AunUpdateTextBrush" Color="{ThemeResource SystemColorWindowTextColor}" />
          <SolidColorBrush x:Key="AunUpdateDimBrush" Color="{ThemeResource SystemColorWindowTextColor}" />
          <SolidColorBrush x:Key="AunUpdateLinkBrush" Color="{ThemeResource SystemColorHotlightColor}" />
        </ResourceDictionary>
        """;

    // CI's scenes
    private static bool IsShown(FrameworkElement e)
    {
        if (e.XamlRoot == null || !e.IsLoaded || e.ActualHeight <= 0) return false;
        for (DependencyObject? at = e; at != null; at = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: Visibility.Collapsed }) return false;
        }

        return true;
    }

    /// <summary>
    /// In high contrast every part that is shown is in the system's colors, and some part is shown. A part that is not in
    /// the window has not got its own resources yet: the app's answer for it.
    /// </summary>
    public bool IsPainted
    {
        get
        {
            if (!new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) return true;
            var system = new global::Windows.UI.ViewManagement.UISettings();
            var parts = new (FrameworkElement Part, Microsoft.UI.Xaml.Media.Brush? Brush, global::Windows.UI.ViewManagement.UIElementType Color)[]
            {
                (Bar, ((Grid)Bar).Background, global::Windows.UI.ViewManagement.UIElementType.Window),
                (_barText, _barText.Foreground, global::Windows.UI.ViewManagement.UIElementType.WindowText),
                (_barNotes, _barNotes.Foreground, global::Windows.UI.ViewManagement.UIElementType.Hotlight),
                (_barDownload, _barDownload.Background, global::Windows.UI.ViewManagement.UIElementType.Highlight),
                (_barDownload, _barDownload.Foreground, global::Windows.UI.ViewManagement.UIElementType.HighlightText),
                (Forced, ((Grid)Forced).Background, global::Windows.UI.ViewManagement.UIElementType.Window),
                (_forcedBody, _forcedBody.Foreground, global::Windows.UI.ViewManagement.UIElementType.WindowText),
                (_forcedDownload, _forcedDownload.Background, global::Windows.UI.ViewManagement.UIElementType.Highlight),
                (_forcedDownload, _forcedDownload.Foreground, global::Windows.UI.ViewManagement.UIElementType.HighlightText),
                (_version, _version.Foreground, global::Windows.UI.ViewManagement.UIElementType.WindowText),
                (_sectionNotes, _sectionNotes.Foreground, global::Windows.UI.ViewManagement.UIElementType.Hotlight),
                (_sectionDownload, _sectionDownload.Background, global::Windows.UI.ViewManagement.UIElementType.Highlight),
                (_sectionDownload, _sectionDownload.Foreground, global::Windows.UI.ViewManagement.UIElementType.HighlightText),
            };
            var shown = parts.Where(p => IsShown(p.Part)).ToList();
            return shown.Count > 0 && shown.All(p => p.Brush is Microsoft.UI.Xaml.Media.SolidColorBrush b && b.Color == system.UIElementColor(p.Color));
        }
    }

    public bool IsBarShown => IsShown(Bar);
    public string BarText => _barText.Text;
    public bool IsCloseShown => IsShown(_close);
    public bool IsDownloadShown => IsShown(_barDownload);
    public bool IsNotesShown => IsShown(_barNotes);
    public string OpenNotes => _openNotes;
    public bool IsForcedShown => IsShown(Forced);
    public string ForcedText => _forcedBody.Text + "\n" + _forcedMessage.Text;

    public bool IsForcedWhole
    {
        get
        {
            if (!IsForcedShown || Forced.XamlRoot is not { } root || Forced.Parent is not Panel parent) return false;
            // The header, and the screen to the window's end: nothing else is in the window
            var others = parent.Children.OfType<FrameworkElement>().Where(c => c != Forced && IsShown(c)).ToList();
            var box = Forced.TransformToVisual(null).TransformBounds(new global::Windows.Foundation.Rect(0, 0, Forced.ActualWidth, Forced.ActualHeight));
            return others.Count <= 1 && box.Top <= 80 && Math.Abs(box.Bottom - root.Size.Height) <= 1 && Math.Abs(box.Width - root.Size.Width) <= 1;
        }
    }

    public bool IsForcedFocused => Forced.XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), _forcedDownload);

    public bool IsSectionShown => IsShown(SectionBody);
    public string SectionText => string.Join("\n", _version.Text, _lastChecked.Text, _result.Text);
    public void PressClose() => Press(_close);
    public void PressNotes() => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new HyperlinkButtonAutomationPeer(_barNotes)).Invoke();
    public void PressDownload() => Press(IsForcedShown ? _forcedDownload : _barDownload);
    public void PressCheckNow() => Press(_checkNow);
    private static void Press(Button button) => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
}
