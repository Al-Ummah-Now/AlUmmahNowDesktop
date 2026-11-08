using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.Platform.Linux;

/// <summary>
/// Stores all keys for one app as a single libsecret item (GNOME Keyring or the Flatpak portal).
/// Calls libsecret directly because its schema API is variadic.
/// </summary>
public sealed class LibSecretStore : ISecureStore
{
    private const string Glib = "libglib-2.0.so.0";
    private const string Secret = "libsecret-1.so.0";
    private const int SchemaFlagsNone = 0;
    private static readonly IntPtr StrHash = NativeLibrary.GetExport(NativeLibrary.Load(Glib), "g_str_hash");
    private static readonly IntPtr StrEqual = NativeLibrary.GetExport(NativeLibrary.Load(Glib), "g_str_equal");

    private readonly string _app;
    private readonly IntPtr _schema;
    private readonly object _gate = new();

    public LibSecretStore(string app)
    {
        _app = app;
        var attributes = g_hash_table_new(StrHash, StrEqual);
        // A value of 0 is SECRET_SCHEMA_ATTRIBUTE_STRING.
        g_hash_table_insert(attributes, StaticString("app"), IntPtr.Zero);
        _schema = secret_schema_newv("net.alummahnow.Tokens", SchemaFlagsNone, attributes);
        g_hash_table_unref(attributes);
    }

    public byte[]? Get(string key)
    {
        lock (_gate) return ReadAll().TryGetValue(key, out var value) ? Convert.FromBase64String(value) : null;
    }

    public void Set(string key, byte[] value)
    {
        lock (_gate)
        {
            var all = ReadAll();
            all[key] = Convert.ToBase64String(value);
            WriteAll(all);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (all.Remove(key)) WriteAll(all);
        }
    }

    public string[] List(string prefix)
    {
        lock (_gate) return ReadAll().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private Dictionary<string, string> ReadAll()
    {
        var attributes = Attributes();
        var result = secret_password_lookupv_sync(_schema, attributes, IntPtr.Zero, out var error);
        g_hash_table_unref(attributes);
        ThrowIfError(error);
        if (result == IntPtr.Zero) return new Dictionary<string, string>();
        var json = Marshal.PtrToStringUTF8(result) ?? "{}";
        secret_password_free(result);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        var attributes = Attributes();
        var ok = secret_password_storev_sync(_schema, attributes, IntPtr.Zero, "Al Ummah Now sign-in", JsonSerializer.Serialize(values), IntPtr.Zero, out var error);
        g_hash_table_unref(attributes);
        ThrowIfError(error);
        if (!ok) throw new IOException("The keyring refused the item");
    }

    private IntPtr Attributes()
    {
        var table = g_hash_table_new(StrHash, StrEqual);
        g_hash_table_insert(table, StaticString("app"), StaticString(_app));
        return table;
    }

    private static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        // GError: guint32 domain, gint code, gchar* message.
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
        g_error_free(error);
        throw new IOException("Keyring error: " + message);
    }

    // Interned for the process lifetime: GHashTable holds these pointers without copying.
    private static readonly Dictionary<string, IntPtr> Interned = new();

    private static IntPtr StaticString(string value)
    {
        lock (Interned)
        {
            if (!Interned.TryGetValue(value, out var pointer))
            {
                var bytes = Encoding.UTF8.GetBytes(value + "\0");
                pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                Interned[value] = pointer;
            }

            return pointer;
        }
    }

    [DllImport(Glib)] private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(Glib)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib)] private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Glib)] private static extern void g_error_free(IntPtr error);
    [DllImport(Secret, CharSet = CharSet.Ansi)] private static extern IntPtr secret_schema_newv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, IntPtr attributes);
    [DllImport(Secret)] private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret)] private static extern void secret_password_free(IntPtr password);
    [DllImport(Secret)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancellable, out IntPtr error);
}
