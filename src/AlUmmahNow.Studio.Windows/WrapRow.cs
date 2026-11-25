using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Controls in a row, and in the next one when they do not fit (WinUI has no wrapping panel of its own).</summary>
internal sealed partial class WrapRow : Panel
{
    private const double Spacing = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double room, bool arrange)
    {
        double x = 0, y = 0, width = 0;
        var row = new List<UIElement>();
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var next = x + (row.Count > 0 ? Spacing : 0) + child.DesiredSize.Width;
            if (row.Count > 0 && next > room + 0.5)
            {
                y += EndRow(row, y, arrange) + Spacing;
                next = child.DesiredSize.Width;
            }

            row.Add(child);
            x = next;
            width = Math.Max(width, x);
        }

        return new Size(Math.Min(width, room), y + EndRow(row, y, arrange));
    }

    /// <summary>Each in the middle of its row's height.</summary>
    private static double EndRow(List<UIElement> row, double y, bool arrange)
    {
        var height = row.Count == 0 ? 0 : row.Max(c => c.DesiredSize.Height);
        double x = 0;
        foreach (var child in row)
        {
            if (arrange) child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
            x += child.DesiredSize.Width + Spacing;
        }

        row.Clear();
        return height;
    }
}
