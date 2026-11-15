using AlUmmahNow.CoreBridge.Logging;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Timeline;

namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// Draws titles with Cairo and Pango: Pango shapes Arabic (joining forms, marks, bidi) and lays each line out in its
/// own direction. The editor preview and the export use this same code, at the picture's size, so they match.
/// </summary>
public sealed class CairoTitlePainter : ITitlePainter
{
    private static readonly Logger Logger = Log.For("titles");

    private byte[]? _logoBytes;
    private GdkPixbuf.Pixbuf? _logo;

    public void WritePng(EdlTitle title, TitleDecor decor, int width, int height, string path)
    {
        using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, width, height);
        using (var cr = new Cairo.Context(surface))
        {
            Paint(cr, title, decor, width, height);
        }

        surface.Flush();
        var status = surface.WriteToPng(path);
        if (status != Cairo.Status.Success) throw new InvalidOperationException("Could not write the title image: " + status);
    }

    /// <summary>Draws one title over a <paramref name="width"/>×<paramref name="height"/> picture in <paramref name="cr"/>'s user space.</summary>
    public void Paint(Cairo.Context cr, EdlTitle title, TitleDecor decor, int width, int height)
    {
        var logo = Logo(decor.Logo);
        var family = title.Style.FontFamily;
        var placement = TitleLayout.Place(title, decor, width, height, (text, rtl, px, bold, max) =>
        {
            var (w, h, _, _) = Measure(cr, family, text, px, bold, max, centered: title.Style.Position == TitlePosition.Center);
            return (w, h);
        }, logo == null ? null : (double)logo.Width / Math.Max(1, logo.Height));

        if (placement.Background is { } box)
        {
            var bg = TitleTemplates.Rgb(placement.BackgroundColor, BrandKit.DefaultSecondary);
            cr.SetSourceRgb(bg.R, bg.G, bg.B);
            cr.Rectangle(box.X, box.Y, box.Width, box.Height);
            cr.Fill();
        }

        if (placement.Accent is { } accent)
        {
            var a = TitleTemplates.Rgb(placement.AccentColor, BrandKit.DefaultPrimary);
            cr.SetSourceRgb(a.R, a.G, a.B);
            cr.Rectangle(accent.X, accent.Y, accent.Width, accent.Height);
            cr.Fill();
        }

        if (placement.Logo is { } l && logo != null)
        {
            cr.Save();
            cr.Translate(l.X, l.Y);
            cr.Scale(l.Width / logo.Width, l.Height / logo.Height);
            Gdk.Functions.CairoSetSourcePixbuf(cr, logo, 0, 0);
            cr.Paint();
            cr.Restore();
        }

        var color = TitleTemplates.Rgb(placement.TextColor, BrandKit.DefaultText);
        foreach (var line in placement.Lines)
        {
            var (_, _, layout, offset) = Measure(cr, family, line.Text, line.FontPx, line.Bold, placement.WrapWidth, placement.Centered);
            var x = line.Bounds.X - offset.X;
            var y = line.Bounds.Y - offset.Y;
            if (placement.Shadow)
            {
                // Text without a box gets a soft shadow so it reads over any picture.
                var d = Math.Max(1, line.FontPx * 0.06);
                cr.SetSourceRgba(0, 0, 0, 0.6);
                cr.MoveTo(x + d, y + d);
                PangoCairo.Functions.ShowLayout(cr, layout);
            }

            cr.SetSourceRgb(color.R, color.G, color.B);
            cr.MoveTo(x, y);
            PangoCairo.Functions.ShowLayout(cr, layout);
        }
    }

    /// <summary>The brand logo, decoded once per image.</summary>
    private GdkPixbuf.Pixbuf? Logo(byte[]? bytes)
    {
        if (bytes == null) return null;
        if (ReferenceEquals(bytes, _logoBytes)) return _logo;
        _logoBytes = bytes;
        _logo = null;
        try
        {
            using var data = GLib.Bytes.New(bytes);
            using var stream = Gio.MemoryInputStream.NewFromBytes(data);
            _logo = GdkPixbuf.Pixbuf.NewFromStream(stream, null);
        }
        catch (GLib.GException ex)
        {
            Logger.Warn("brand logo is not an image this computer can read", ex);
        }

        return _logo;
    }

    /// <summary>
    /// A wrapped Pango layout of one line: its size, and the offset of its text inside the layout (an Arabic line in
    /// a wrapped layout is aligned to the right, so its text doesn't start at x = 0).
    /// </summary>
    private static (double Width, double Height, Pango.Layout Layout, (double X, double Y) Offset) Measure(Cairo.Context cr, string family, string text, double px, bool bold, double maxWidth, bool centered)
    {
        var layout = PangoCairo.Functions.CreateLayout(cr);
        var families = string.IsNullOrWhiteSpace(family) ? TitleTemplates.FontFamily : family;
        var font = Pango.FontDescription.FromString(families + ", Noto Sans, Sans" + (bold ? " Bold" : ""));
        font.SetAbsoluteSize(px * Pango.Constants.SCALE);
        layout.SetFontDescription(font);
        layout.SetAutoDir(true);
        layout.SetAlignment(centered ? Pango.Alignment.Center : Pango.Alignment.Left);
        layout.SetWrap(Pango.WrapMode.WordChar);
        layout.SetWidth((int)Math.Max(1, maxWidth * Pango.Constants.SCALE));
        layout.SetText(text, -1);
        layout.GetPixelExtents(out _, out var logical);
        return (logical.Width, logical.Height, layout, (logical.X, logical.Y));
    }
}
