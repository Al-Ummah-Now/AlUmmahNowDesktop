// D9 validation: exercises the Gir.Core surface that Epic 19.5 (the editor) depends on.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

var lang = Arg("--lang", "ar");
var seconds = int.Parse(Arg("--seconds", "25"));
var reportPath = Arg("--report", "d9-report.txt");
var report = new List<string>();
void Log(string s) { Console.WriteLine(s); report.Add(s); }
string Arg(string n, string d) { var i = Array.IndexOf(args, n); return i >= 0 ? args[i + 1] : d; }

Adw.Module.Initialize();
Gst.Module.Initialize();
var argv = Array.Empty<string>();
Gst.Functions.Init(ref argv);

var hls = "";
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    // The list endpoint filters after paging, so a page can be short: follow the cursor.
    string? cursor = null;
    for (var page = 0; page < 10 && hls.Length == 0; page++)
    {
        var url = "https://videos.api.qa.alummahnow.net/api/videos?limit=50" + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        foreach (var v in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (v.GetProperty("uploadStatus").GetString() == "ready" && v.TryGetProperty("playUrl", out var u) && v.GetProperty("title").GetString()!.StartsWith("QA "))
            {
                hls = u.GetString()!;
                break;
            }
        }

        cursor = doc.RootElement.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (cursor == null) break;
    }

    if (hls.Length == 0) throw new InvalidOperationException("no ready QA video found");
    Log("QA HLS url: resolved");
}
catch (Exception ex) { Log("QA HLS url: FAILED " + ex.Message); }

var app = Adw.Application.New("net.alummahnow.spike.d9", Gio.ApplicationFlags.NonUnique);
Gst.Element? playbin = null;
long frames = 0, gcRounds = 0;
var snapshotCalls = 0L;
double snapshotMs = 0, cairoMs = 0;
long cairoCalls = 0;
Gst.State last = Gst.State.Null;

app.OnActivate += (_, _) =>
{
    Gtk.Widget.SetDefaultDirection(lang == "ar" ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);
    var win = Adw.ApplicationWindow.New(app);
    win.SetDefaultSize(1280, 800);

    var root = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    var header = Adw.HeaderBar.New();
    root.Append(header);

    var body = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    body.SetVexpand(true);
    root.Append(body);

    // 1. Video through gtk4paintablesink into a Gtk.Picture.
    var left = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    left.SetHexpand(true);
    body.Append(left);
    var picture = Gtk.Picture.New();
    picture.SetVexpand(true);
    picture.SetSizeRequest(640, 360);
    left.Append(picture);

    var sink = Gst.ElementFactory.Make("gtk4paintablesink", "sink");
    Log("gtk4paintablesink element: " + (sink != null ? "PASS" : "FAIL (plugin missing)"));
    if (sink != null)
    {
        using var value = new GObject.Value(GObject.Type.Object);
        sink.GetProperty("paintable", value);
        var obj = value.GetObject();
        Log("paintable managed type: " + obj?.GetType().FullName);
        if (obj is Gdk.Paintable p)
        {
            picture.SetPaintable(p);
            Log("paintable cast to Gdk.Paintable: PASS");
            p.OnInvalidateContents += (_, _) => frames++;
        }
        else if (obj != null)
        {
            // The generated PaintableHelper constructor is not public, so an unknown GType needs reflection.
            var wrapped = (Gdk.PaintableHelper)Activator.CreateInstance(
                typeof(Gdk.PaintableHelper),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new object[] { obj.Handle }, null)!;
            picture.SetPaintable(wrapped);
            Log("paintable cast to Gdk.Paintable: needed PaintableHelper workaround");
            wrapped.OnInvalidateContents += (_, _) => frames++;
        }

        playbin = Gst.ElementFactory.Make("playbin3", "player") ?? Gst.ElementFactory.Make("playbin", "player");
        if (playbin != null)
        {
            playbin.SetProperty("video-sink", new GObject.Value(sink));
            playbin.SetProperty("uri", new GObject.Value(hls));
            var bus = playbin.GetBus()!;
            bus.AddWatch(0, (busObj, msg) =>
            {
                if (msg.Type == Gst.MessageType.Error)
                {
                    // Gir.Core 0.8.1 does not bind gst_message_parse_error.
                    D9.GstNative.gst_message_parse_error(msg.Handle.DangerousGetHandle(), out var error, out var debug);
                    var text = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) : "";
                    Log("gst error: " + text + " | " + Marshal.PtrToStringUTF8(debug));
                }

                if (msg.Type == Gst.MessageType.StateChanged)
                {
                    msg.ParseStateChanged(out _, out var now, out _);
                    last = now;
                }

                return true;
            });
            playbin.SetState(Gst.State.Playing);
        }
    }

    // 2. RTL Arabic text.
    var label = Gtk.Label.New("بسم الله الرحمن الرحيم — مرحباً بكم في الأمة الآن (Al Ummah Now) ١٢٣");
    label.SetWrap(true);
    label.AddCssClass("title-2");
    left.Append(label);

    // 3. ListView with a managed GObject subclass and 2000 rows.
    var store = Gio.ListStore.New(D9.Row.GetGType());
    for (var i = 0; i < 2000; i++) store.Append(D9.Row.Create((i % 2 == 0 ? "محاضرة رقم " : "Lecture ") + i));
    var factory = Gtk.SignalListItemFactory.New();
    factory.OnSetup += (_, a) => ((Gtk.ListItem)a.Object).SetChild(Gtk.Label.New(""));
    factory.OnBind += (_, a) =>
    {
        var li = (Gtk.ListItem)a.Object;
        ((Gtk.Label)li.GetChild()!).SetText(((D9.Row)li.GetItem()!).Text);
    };
    var list = Gtk.ListView.New(Gtk.SingleSelection.New(store), factory);
    var scroll = Gtk.ScrolledWindow.New();
    scroll.SetChild(list);
    scroll.SetSizeRequest(300, -1);
    body.Append(scroll);
    Log("ListView + managed GObject subclass (2000 rows): built");

    // 4. Timeline by cairo with a drag gesture.
    double playhead = 100;
    var area = Gtk.DrawingArea.New();
    area.SetContentHeight(90);
    area.SetDrawFunc((_, cr, w, h) =>
    {
        var sw = Stopwatch.StartNew();
        cr.SetSourceRgb(0.04, 0.19, 0.25);
        cr.Rectangle(0, 0, w, h);
        cr.Fill();
        for (var i = 0; i < 2000; i++)
        {
            cr.SetSourceRgb(0.56, 0.42 + (i % 5) * 0.05, 0.33);
            cr.Rectangle(i * 3 % w, 10 + (i % 3) * 25, 2.5, 20);
            cr.Fill();
        }

        cr.SetSourceRgb(1, 0.63, 0.22);
        cr.Rectangle(playhead, 0, 2, h);
        cr.Fill();
        cairoMs += sw.Elapsed.TotalMilliseconds;
        cairoCalls++;
    });
    var drag = Gtk.GestureDrag.New();
    drag.OnDragUpdate += (g, a) => { playhead = Math.Max(0, playhead + a.OffsetX); area.QueueDraw(); };
    area.AddController(drag);
    root.Append(area);

    // 5. GPU timeline: hand-installed GtkWidget.snapshot vfunc.
    var gpu = D9.SnapshotWidget.NewWithProperties([]);
    gpu.SetSizeRequest(-1, 60);
    D9.SnapshotWidget.Render = (ms) => { snapshotCalls++; snapshotMs += ms; };
    root.Append(gpu);
    Log("snapshot vfunc install: " + D9.SnapshotWidget.Install(gpu));

    win.SetContent(root);
    win.Present();

    // Stress the wrappers and finalizers while media runs; binding lifetime bugs surface here.
    GLib.Functions.TimeoutAdd(0, 200, () =>
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        gcRounds++;
        area.QueueDraw();
        gpu.QueueDraw();
        return true;
    });

    // Seek and rate change: both are core to a scrubbing editor.
    GLib.Functions.TimeoutAdd(0, 9000, () =>
    {
        var ok = playbin?.Seek(1.5, Gst.Format.Time, Gst.SeekFlags.Flush | Gst.SeekFlags.Accurate, Gst.SeekType.Set, 30_000_000_000L, Gst.SeekType.None, -1) ?? false;
        Log("seek to 30s at 1.5x accepted: " + ok);
        return false;
    });

    GLib.Functions.TimeoutAdd(0, (uint)seconds * 1000, () =>
    {
        long position = 0;
        var hasPos = playbin?.QueryPosition(Gst.Format.Time, out position) ?? false;
        Log($"playbin state at end: {last}; position ok={hasPos}; seconds={position / 1e9:F1}");
        Log($"video frames invalidated: {frames}");
        Log($"GC stress rounds survived: {gcRounds}");
        Log($"cairo timeline: {cairoCalls} draws, avg {(cairoCalls > 0 ? cairoMs / cairoCalls : 0):F2} ms (2000 clips)");
        Log($"snapshot timeline: {snapshotCalls} draws, avg {(snapshotCalls > 0 ? snapshotMs / snapshotCalls : 0):F2} ms (2000 clips)");
        Log("direction: " + Gtk.Widget.GetDefaultDirection());
        var advanced = hasPos && position > 30_000_000_000L && frames > 0;
        Log("VIDEO VERDICT: " + (advanced ? "PASS" : "FAIL"));
        File.WriteAllLines(reportPath, report);
        Environment.ExitCode = advanced ? 0 : 3;
        playbin?.SetState(Gst.State.Null);
        app.Quit();
        return false;
    });
};

var rc = app.RunWithSynchronizationContext(null);
return rc != 0 ? rc : Environment.ExitCode;

namespace D9
{
    internal static class GstNative
    {
        [DllImport("libgstreamer-1.0.so.0")]
        internal static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
    }

    [GObject.Subclass<GObject.Object>]
    public partial class Row
    {
        public string Text { get; private set; } = "";

        public static Row Create(string text)
        {
            var row = NewWithProperties([]);
            row.Text = text;
            return row;
        }
    }

    [GObject.Subclass<Gtk.Widget>]
    public partial class SnapshotWidget
    {
        public static Action<double>? Render;
        private static Gtk.Internal.WidgetClassData.SnapshotCallback? _callback;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Native();

        [DllImport("libgtk-4.so.1")] private static extern void gtk_snapshot_append_color(IntPtr s, ref Rgba c, ref Rect r);
        [DllImport("libgtk-4.so.1")] private static extern int gtk_widget_get_width(IntPtr w);

        [StructLayout(LayoutKind.Sequential)] private struct Rgba { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public float X, Y, W, H; }

        public static string Install(SnapshotWidget instance)
        {
            try
            {
                var handle = instance.Handle.DangerousGetHandle();
                var klass = Marshal.ReadIntPtr(handle);
                var offset = Marshal.OffsetOf<Gtk.Internal.WidgetClassData>(nameof(Gtk.Internal.WidgetClassData.Snapshot));
                _callback = (w, snap) =>
                {
                    var sw = Stopwatch.StartNew();
                    var width = Math.Max(1, gtk_widget_get_width(w));
                    for (var i = 0; i < 2000; i++)
                    {
                        var c = new Rgba { R = 1f, G = 0.63f, B = 0.22f, A = 1f };
                        var r = new Rect { X = i * 3 % width, Y = 5 + (i % 3) * 18, W = 2.5f, H = 14 };
                        gtk_snapshot_append_color(snap, ref c, ref r);
                    }

                    Render?.Invoke(sw.Elapsed.TotalMilliseconds);
                };
                Marshal.WriteIntPtr(klass, (int)offset, Marshal.GetFunctionPointerForDelegate(_callback));
                return "PASS (requires Gtk.Internal class struct and P/Invoke; no managed API)";
            }
            catch (Exception ex)
            {
                return "FAIL " + ex.Message;
            }
        }
    }
}
