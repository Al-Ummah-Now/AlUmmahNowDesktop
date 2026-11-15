using AlUmmahNow.App.I18n;
using AlUmmahNow.Studio.Encode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = System.IO.Path;

namespace AlUmmahNow.Studio.WindowsApp;

internal static class Rows
{
    public static FrameworkElement Progress(string name, double fraction, string status)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, name);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        var text = new TextBlock { Text = status };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>Encode queue (spike).</summary>
internal sealed class EncodePage
{
    private readonly Localizer _l;
    private readonly EncodeQueue _encode;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private int _dirty = 1;

    public EncodePage(Localizer l, EncodeQueue encode)
    {
        _l = l;
        _encode = encode;
        Root = new StackPanel { Padding = new Thickness(16) };
        Root.Children.Add(_rows);
        encode.Changed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
    }

    public StackPanel Root { get; }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _rows.Children.Clear();
        foreach (var job in _encode.Jobs.ToList())
        {
            var status = job.Status switch
            {
                EncodeStatus.Running => _l["studio.encode.running"],
                EncodeStatus.Completed => _l["studio.status.completed"],
                EncodeStatus.Failed => _l["studio.status.failed"],
                EncodeStatus.Canceled => _l["studio.encode.canceled"],
                _ => _l["studio.status.queued"],
            };
            var name = Path.GetFileName(job.Request.Source) + " · " + _l["studio.preset." + job.Request.Preset.Key];
            var encoder = job.UsedEncoder is { } e ? " " + _l[e.Kind == EncoderKind.Hardware ? "studio.encode.hardware" : "studio.encode.software"] : "";
            _rows.Children.Add(Rows.Progress(name, job.Progress, status + encoder));
        }
    }
}
