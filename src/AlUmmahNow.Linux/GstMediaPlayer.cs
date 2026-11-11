using AlUmmahNow.CoreBridge.Logging;
using System.Runtime.InteropServices;
using AlUmmahNow.App.Media;

namespace AlUmmahNow.Linux;

/// <summary>playbin3 rendering into a gtk4paintablesink. All members run on the GTK main thread.</summary>
public sealed class GstMediaPlayer : IMediaPlayer
{
    private const long NanosPerSecond = 1_000_000_000L;

    private readonly Gst.Element _playbin;
    private double _rate = 1;
    private double _pendingStart = -1;
    private bool _wantPlaying;
    private bool _open;
    private PlayerState _state = PlayerState.Idle;

    public GstMediaPlayer()
    {
        var sink = Gst.ElementFactory.Make("gtk4paintablesink", "sink")
            ?? throw new InvalidOperationException("gtk4paintablesink is not installed");
        using (var value = new GObject.Value(GObject.Type.Object))
        {
            sink.GetProperty("paintable", value);
            Paintable = value.GetObject() as Gdk.Paintable
                ?? throw new InvalidOperationException("sink did not expose a paintable");
        }

        _playbin = Gst.ElementFactory.Make("playbin3", "player") ?? Gst.ElementFactory.Make("playbin", "player")
            ?? throw new InvalidOperationException("playbin is not installed");
        _playbin.SetProperty("video-sink", new GObject.Value(sink));

        var bus = _playbin.GetBus() ?? throw new InvalidOperationException("playbin has no bus");
        bus.AddWatch(0, (_, message) => OnBusMessage(message));
        GLib.Functions.TimeoutAdd(0, 250, Tick);
    }

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;

    public Gdk.Paintable Paintable { get; }
    public PlayerState State => _state;
    public double Position { get; private set; }
    public double Duration { get; private set; }

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = value;
            if (_open) SeekInternal(Position);
        }
    }

    public void Open(string url, double startPosition, bool autoPlay)
    {
        _playbin.SetState(Gst.State.Ready);
        _playbin.SetProperty("uri", new GObject.Value(url));
        _open = true;
        _pendingStart = startPosition > 0 || Math.Abs(_rate - 1) > 0.001 ? startPosition : -1;
        _wantPlaying = autoPlay;
        Position = startPosition;
        SetState(PlayerState.Loading);
        _playbin.SetState(Gst.State.Paused);
    }

    public void Play()
    {
        _wantPlaying = true;
        if (_open) _playbin.SetState(Gst.State.Playing);
    }

    public void Pause()
    {
        _wantPlaying = false;
        if (_open) _playbin.SetState(Gst.State.Paused);
    }

    public void Seek(double position)
    {
        Position = position;
        if (_open) SeekInternal(position);
    }

    public void Stop()
    {
        _open = false;
        _wantPlaying = false;
        _playbin.SetState(Gst.State.Null);
        SetState(PlayerState.Idle);
    }

    private void SeekInternal(double position) =>
        _playbin.Seek(_rate, Gst.Format.Time, Gst.SeekFlags.Flush | Gst.SeekFlags.KeyUnit, Gst.SeekType.Set, (long)(position * NanosPerSecond), Gst.SeekType.None, -1);

    private bool OnBusMessage(Gst.Message message)
    {
        switch (message.Type)
        {
            case Gst.MessageType.AsyncDone:
                if (_pendingStart >= 0)
                {
                    var start = _pendingStart;
                    _pendingStart = -1;
                    SeekInternal(start);
                }

                if (_open) _playbin.SetState(_wantPlaying ? Gst.State.Playing : Gst.State.Paused);
                break;
            case Gst.MessageType.Eos:
                SetState(PlayerState.Ended);
                break;
            case Gst.MessageType.Error:
                Log.For("player").Error("playback error: " + ReadError(message));
                _open = false;
                SetState(PlayerState.Failed);
                break;
        }

        return true;
    }

    private bool Tick()
    {
        if (!_open) return true;

        long value = 0;
        if (_playbin.QueryPosition(Gst.Format.Time, out value) && value >= 0) Position = value / (double)NanosPerSecond;
        if (_playbin.QueryDuration(Gst.Format.Time, out value) && value > 0) Duration = value / (double)NanosPerSecond;
        PositionChanged?.Invoke(this, EventArgs.Empty);

        _playbin.GetState(out var current, out var pending, 0);
        if (pending == Gst.State.VoidPending && _state != PlayerState.Ended)
        {
            if (current == Gst.State.Playing) SetState(PlayerState.Playing);
            else if (current == Gst.State.Paused && !_wantPlaying) SetState(PlayerState.Paused);
        }

        return true;
    }

    private void SetState(PlayerState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Gir.Core 0.8.1 does not bind gst_message_parse_error (docs/D9-FINDING.md).
    [DllImport("libgstreamer-1.0.so.0")]
    private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    private static string ReadError(Gst.Message message)
    {
        gst_message_parse_error(message.Handle.DangerousGetHandle(), out var error, out var debug);
        var text = error != IntPtr.Zero ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) : "";
        return text + " | " + Marshal.PtrToStringUTF8(debug);
    }
}
