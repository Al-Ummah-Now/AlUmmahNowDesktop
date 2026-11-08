using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AlUmmahNow.Core;

namespace AlUmmahNow.CoreBridge.Native;

/// <summary>Entry points into the Rust core. v0.1.0 exposes the version, adapters and self-tests.</summary>
public static class CoreRuntime
{
    public static string Version() => AunCoreMethods.CoreVersion();

    /// <summary>Round-trips the store through the core. Throws if the adapter is broken.</summary>
    public static byte[] SelfTestSecureStorage(ISecureStore store) =>
        AunCoreMethods.SelftestSecureStorage(new SecureStorageAdapter(store));

    public static NetworkState SelfTestConnectivity() =>
        AunCoreMethods.SelftestConnectivity(new SystemConnectivity());

    /// <summary>Reports the core version, tolerating a platform with no native library.</summary>
    public static string VersionOrNone()
    {
        try
        {
            return Version();
        }
        catch (DllNotFoundException)
        {
            return "unavailable";
        }
    }
}

/// <summary>Presents a platform store to the core.</summary>
public sealed class SecureStorageAdapter : SecureStorage
{
    private readonly ISecureStore _store;

    public SecureStorageAdapter(ISecureStore store) => _store = store;

    public byte[]? Get(string key) => _store.Get(key);
    public void Set(string key, byte[] value) => _store.Set(key, value);
    public void Delete(string key) => _store.Delete(key);
    public string[] List(string prefix) => _store.List(prefix);
}

public sealed class SystemConnectivity : Connectivity
{
    public NetworkState Current() => new(NetworkInterface.GetIsNetworkAvailable(), false);
}

/// <summary>Opens the default browser through the OS.</summary>
public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}

/// <summary>Windows: one DPAPI-protected file per key, readable only by the current user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecureStore : ISecureStore
{
    private readonly string _directory;

    public DpapiSecureStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser) : null;
    }

    public void Set(string key, byte[] value) =>
        File.WriteAllBytes(PathFor(key), ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser));

    public void Delete(string key) => File.Delete(PathFor(key));

    public string[] List(string prefix) =>
        Directory.EnumerateFiles(_directory, "*.bin")
            .Select(f => Encoding.UTF8.GetString(Convert.FromHexString(Path.GetFileNameWithoutExtension(f))))
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

    private string PathFor(string key) => Path.Combine(_directory, Convert.ToHexString(Encoding.UTF8.GetBytes(key)) + ".bin");
}
