using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>
/// Draws titles with GDI+, whose text goes through Uniscribe: Arabic joins and shapes, and each line runs in its own
/// direction. The editor preview and the export both use this painter at the picture's size, so they match.
/// </summary>
public sealed class GdiTitlePainter : ITitlePainter
{
    private static readonly Logger Logger = Log.For("titles");

    /// <summary>Used when the title's family isn't installed; it has Arabic and Latin.</summary>
    private const string Fallback = "Segoe UI";

    private readonly object _gate = new();
    private byte[]? _logoBytes;
    private Image? _logo;

    public void WritePng(EdlTitle title, TitleDecor decor, int width, int height, string path)
    {
        using var bitmap = Render(new[] { title }, decor, width, height);
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>The titles on a transparent picture of the given size (the preview overlays this on the frame).</summary>
    public Bitmap Render(IEnumerable<EdlTitle> titles, TitleDecor decor, int width, int height)
    {
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Grey-scale antialiasing: ClearType needs an opaque background.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        lock (_gate)
        {
            foreach (var title in titles) Paint(g, title, decor, width, height);
        }

        return bitmap;
    }

    private void Paint(Graphics g, EdlTitle title, TitleDecor decor, int width, int height)
    {
        var logo = Logo(decor.Logo);
        using var family = Family(title.Style.FontFamily);
        var centered = title.Style.Position == TitlePosition.Center;
        var placement = TitleLayout.Place(title, decor, width, height, (text, rtl, px, bold, max) =>
        {
            using var font = Font(family, px, bold);
            using var format = Format(rtl, centered);
            var size = g.MeasureString(text, font, new SizeF((float)max, 100_000), format);
            return (Math.Ceiling(size.Width), Math.Ceiling(size.Height));
        }, logo == null ? null : (double)logo.Width / Math.Max(1, logo.Height));

        if (placement.Background is { } box)
        {
            using var brush = new SolidBrush(ColorOf(placement.BackgroundColor, BrandKit.DefaultSecondary));
            g.FillRectangle(brush, Rect(box));
        }

        if (placement.Accent is { } accent)
        {
            using var brush = new SolidBrush(ColorOf(placement.AccentColor, BrandKit.DefaultPrimary));
            g.FillRectangle(brush, Rect(accent));
        }

        if (placement.Logo is { } l && logo != null) g.DrawImage(logo, Rect(l));

        using var text = new SolidBrush(ColorOf(placement.TextColor, BrandKit.DefaultText));
        using var shadow = new SolidBrush(Color.FromArgb(153, 0, 0, 0));
        foreach (var line in placement.Lines)
        {
            using var font = Font(family, line.FontPx, line.Bold);
            using var format = Format(line.IsRtl, centered);
            // The line's own box, one pixel wider so rounding never wraps it again.
            var r = new RectangleF((float)line.Bounds.X, (float)line.Bounds.Y, (float)line.Bounds.Width + 1, (float)line.Bounds.Height + 1);
            if (placement.Shadow)
            {
                var d = (float)Math.Max(1, line.FontPx * 0.06);
                g.DrawString(line.Text, font, shadow, new RectangleF(r.X + d, r.Y + d, r.Width, r.Height), format);
            }

            g.DrawString(line.Text, font, text, r, format);
        }
    }

    private static Font Font(FontFamily family, double px, bool bold) =>
        new(family, (float)Math.Max(1, px), family.IsStyleAvailable(bold ? FontStyle.Bold : FontStyle.Regular) && bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Wraps at the box width; right-to-left lines align to the right.</summary>
    private static StringFormat Format(bool rtl, bool centered)
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags &= ~StringFormatFlags.NoWrap;
        if (rtl) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        format.Alignment = centered ? StringAlignment.Center : StringAlignment.Near;
        format.Trimming = StringTrimming.None;
        return format;
    }

    private static FontFamily Family(string name)
    {
        try
        {
            return new FontFamily(string.IsNullOrWhiteSpace(name) ? TitleTemplates.FontFamily : name);
        }
        catch (ArgumentException)
        {
            return new FontFamily(Fallback);
        }
    }

    private Image? Logo(byte[]? bytes)
    {
        if (bytes == null) return null;
        lock (_gate)
        {
            if (ReferenceEquals(bytes, _logoBytes)) return _logo;
            _logoBytes = bytes;
            _logo?.Dispose();
            _logo = null;
            try
            {
                // GDI+ keeps reading the stream for the image's lifetime, so it stays open with it.
                _logo = Image.FromStream(new MemoryStream(bytes));
            }
            catch (ArgumentException ex)
            {
                Logger.Warn("brand logo is not an image this computer can read", ex);
            }

            return _logo;
        }
    }

    private static RectangleF Rect(TitleRect r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static Color ColorOf(string? hex, string fallback)
    {
        var (r, g, b) = TitleTemplates.Rgb(hex, fallback);
        return Color.FromArgb(255, (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }
}
