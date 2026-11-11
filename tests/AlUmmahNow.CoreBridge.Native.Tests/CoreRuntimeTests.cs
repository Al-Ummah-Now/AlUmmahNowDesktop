using System.Runtime.InteropServices;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Native;
using AlUmmahNow.Platform.Linux;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

public class CoreRuntimeTests
{
    private sealed class MemoryStore : ISecureStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public byte[]? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, byte[] value) => _items[key] = value;
        public void Delete(string key) => _items.Remove(key);
        public string[] List(string prefix) => _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public void Native_core_loads_and_reports_its_version() =>
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", CoreRuntime.Version());

    [Fact]
    public void Core_round_trips_through_the_storage_adapter() => Assert.NotEmpty(CoreRuntime.SelfTestSecureStorage(new MemoryStore()));

    [Fact]
    public void Core_reads_connectivity_through_the_adapter() => Assert.False(CoreRuntime.SelfTestConnectivity().Metered);

    [Fact]
    public void Libsecret_store_round_trips_on_a_real_keyring()
    {
        // CI starts gnome-keyring for this; a developer machine without one is skipped.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || Environment.GetEnvironmentVariable("AUN_KEYRING_TEST") != "1") return;
        var store = new LibSecretStore("alummahnow-ci-" + Guid.NewGuid().ToString("N"));
        Assert.Null(store.Get("auth.a"));
        store.Set("auth.a", new byte[] { 1, 2, 3 });
        store.Set("other", new byte[] { 9 });
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Get("auth.a"));
        Assert.Equal(new[] { "auth.a" }, store.List("auth."));
        store.Delete("auth.a");
        Assert.Null(store.Get("auth.a"));
        Assert.NotEmpty(CoreRuntime.SelfTestSecureStorage(store));
    }
}
