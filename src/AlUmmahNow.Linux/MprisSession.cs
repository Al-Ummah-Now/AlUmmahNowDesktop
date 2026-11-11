using AlUmmahNow.CoreBridge.Logging;
using System.Globalization;
using AlUmmahNow.App.Media;
using Tmds.DBus.Protocol;

namespace AlUmmahNow.Linux;

/// <summary>Publishes the player over MPRIS so desktop media keys and shell widgets can control it.</summary>
public sealed class MprisSession : IMediaSession, IPathMethodHandler
{
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly SynchronizationContext _ui;
    private readonly object _gate = new();
    private DBusConnection? _connection;
    private string _title = "";
    private string _artist = "";
    private string? _art;
    private double _duration;
    private string _status = "Stopped";
    private double _position;
    private double _rate = 1;

    public MprisSession(SynchronizationContext ui) => _ui = ui;

    public event EventHandler<MediaCommand>? Command;

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    /// <summary>Connects in the background. A desktop without a session bus simply has no MPRIS.</summary>
    public async Task StartAsync()
    {
        try
        {
            var address = DBusAddress.Session ?? throw new InvalidOperationException("no session bus");
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(this);
            await connection.RequestNameAsync("org.mpris.MediaPlayer2.alummahnow", RequestNameOptions.None);
            _connection = connection;
            Log.For("mpris").Info("registered");
        }
        catch (Exception ex) when (ex is DBusConnectionException || ex is InvalidOperationException || ex is IOException)
        {
            Log.For("mpris").Warn("unavailable", ex);
        }
    }

    public void SetMetadata(string title, string artist, string? artworkUrl, double duration)
    {
        lock (_gate)
        {
            _title = title;
            _artist = artist;
            _art = artworkUrl;
            _duration = duration;
        }

        EmitChanged("Metadata");
    }

    public void SetPlayback(PlayerState state, double position, double rate)
    {
        lock (_gate)
        {
            _status = state == PlayerState.Playing ? "Playing" : state == PlayerState.Paused || state == PlayerState.Loading ? "Paused" : "Stopped";
            _position = position;
            _rate = rate;
        }

        EmitChanged("PlaybackStatus");
    }

    public void Clear()
    {
        lock (_gate)
        {
            _title = _artist = "";
            _art = null;
            _duration = 0;
            _status = "Stopped";
        }

        EmitChanged("Metadata");
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        var member = request.MemberAsString;
        var iface = request.InterfaceAsString;

        if (context.IsDBusIntrospectRequest)
        {
            ReplyIntrospect(context);
        }
        else if (iface == PropertiesInterface)
        {
            HandleProperties(context, member);
        }
        else if (iface == PlayerInterface && TryMap(member, out var command))
        {
            _ui.Post(_ => Command?.Invoke(this, command), null);
            ReplyEmpty(context);
        }
        else if (iface == RootInterface && (member == "Raise" || member == "Quit"))
        {
            ReplyEmpty(context);
        }
        else
        {
            context.ReplyUnknownMethodError();
        }

        return default;
    }

    internal static bool TryMap(string? member, out MediaCommand command)
    {
        switch (member)
        {
            case "Play": command = MediaCommand.Play; return true;
            case "Pause": case "Stop": command = MediaCommand.Pause; return true;
            case "PlayPause": command = MediaCommand.Toggle; return true;
            case "Next": command = MediaCommand.Next; return true;
            case "Previous": command = MediaCommand.Previous; return true;
            default: command = default; return false;
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null!);
        context.Reply(writer.CreateMessage());
    }

    private void HandleProperties(MethodContext context, string? member)
    {
        var reader = context.Request.GetBodyReader();
        var iface = reader.ReadString();
        var values = iface == RootInterface ? RootProperties() : PlayerProperties();

        if (member == "GetAll")
        {
            using var writer = context.CreateReplyWriter("a{sv}");
            writer.WriteDictionary(values);
            context.Reply(writer.CreateMessage());
        }
        else if (member == "Get" && values.TryGetValue(reader.ReadString(), out var value))
        {
            using var writer = context.CreateReplyWriter("v");
            writer.WriteVariant(value);
            context.Reply(writer.CreateMessage());
        }
        else
        {
            context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Unsupported property request");
        }
    }

    private static Dictionary<string, VariantValue> RootProperties() => new()
    {
        ["CanQuit"] = false,
        ["CanRaise"] = false,
        ["HasTrackList"] = false,
        ["Identity"] = "Al Ummah Now",
        ["DesktopEntry"] = "net.alummahnow.AlUmmahNow",
        ["SupportedUriSchemes"] = new Array<string>(),
        ["SupportedMimeTypes"] = new Array<string>(),
    };

    private Dictionary<string, VariantValue> PlayerProperties()
    {
        lock (_gate)
        {
            var metadata = new Dict<string, VariantValue>
            {
                ["mpris:trackid"] = VariantValue.ObjectPath("/net/alummahnow/track/current"),
                ["xesam:title"] = _title,
                ["xesam:artist"] = new Array<string> { _artist },
                ["mpris:length"] = (long)(_duration * 1_000_000),
            };
            if (_art != null) metadata["mpris:artUrl"] = _art;

            return new Dictionary<string, VariantValue>
            {
                ["PlaybackStatus"] = _status,
                ["Rate"] = _rate,
                ["Metadata"] = metadata.AsVariantValue(),
                ["Position"] = (long)(_position * 1_000_000),
                ["CanGoNext"] = true,
                ["CanGoPrevious"] = true,
                ["CanPlay"] = true,
                ["CanPause"] = true,
                ["CanSeek"] = false,
                ["CanControl"] = true,
                ["MinimumRate"] = 0.5,
                ["MaximumRate"] = 2.0,
            };
        }
    }

    private void EmitChanged(string property)
    {
        var connection = _connection;
        if (connection == null) return;
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
        writer.WriteString(PlayerInterface);
        writer.WriteDictionary(new Dictionary<string, VariantValue> { [property] = PlayerProperties()[property] });
        writer.WriteArray(Array.Empty<string>());
        connection.TrySendMessage(writer.CreateMessage());
    }

    private static void ReplyIntrospect(MethodContext context)
    {
        var xml = System.Text.Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture,
            "<interface name=\"{0}\"><method name=\"Raise\"/><method name=\"Quit\"/></interface>" +
            "<interface name=\"{1}\"><method name=\"Play\"/><method name=\"Pause\"/><method name=\"PlayPause\"/><method name=\"Stop\"/><method name=\"Next\"/><method name=\"Previous\"/></interface>",
            RootInterface, PlayerInterface));
        ReadOnlyMemory<byte>[] parts = { xml };
        context.ReplyIntrospectXml(parts, (IList<string>)Array.Empty<string>());
    }
}
