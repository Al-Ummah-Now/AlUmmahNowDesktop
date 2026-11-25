using System.Text.Json;
using AlUmmahNow.CoreBridge.Native;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>The update state is saved after every check of a manifest, through the real core.</summary>
public sealed class SavedStateTests
{
    private readonly JsonElement _vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-manifests", "vector.json"))).RootElement;

    private string Text(string name) => _vector.GetProperty(name).GetString()!;

    private async Task<CoreUpdateChecker> StartAsync(string? state)
    {
        var answer = JsonSerializer.Serialize(new
        {
            update = new
            {
                enabled = true, status = "recommended", minimumVersion = "0.0.0", latestVersion = "1.6.0", channel = "stable", action = "install", tag = "v1.6.0", signed = true, available = true, checkHours = 6,
                keys = new { list = Text("keyList"), sig = Text("keyListSig") },
            },
            config = new { },
        });
        var checker = new CoreUpdateChecker(_ => Task.FromResult(answer), "1.4.2", state, new[] { Text("rootPublicKey") }, hasKeyList: false);
        Assert.True((await checker.CheckAsync()).CanInstall);
        return checker;
    }

    private void Verify(CoreUpdateChecker checker, string version)
    {
        var m = _vector.GetProperty("manifests").GetProperty(version);
        Assert.Equal(version, checker.VerifyManifest(System.Text.Encoding.UTF8.GetBytes(m.GetProperty("text").GetString()!), m.GetProperty("sig").GetString()!, "v" + version).Version);
    }

    private string Refused(CoreUpdateChecker checker, string version) => Assert.Throws<UpdateRefusedException>(() => Verify(checker, version)).Reason;

    [Fact]
    public async Task What_a_manifest_taught_is_there_to_save_when_its_check_returns_and_holds_after_a_restart()
    {
        // A new install takes any of them
        foreach (var version in new[] { "1.5.0", "1.6.0", "1.6.2" }) Verify(await StartAsync(null), version);

        var first = await StartAsync(null);
        var before = first.State();
        string? saved = null;
        first.Checked += () => saved = first.State();
        Verify(first, "1.6.0");
        Assert.NotNull(saved);
        Assert.NotEqual(before, saved);

        var second = await StartAsync(saved);
        Assert.Equal("below_floor", Refused(second, "1.5.0"));
        Assert.Equal("yanked", Refused(second, "1.6.2"));
        Verify(second, "1.6.0");
    }

    [Fact]
    public async Task A_check_that_refuses_is_told_of_too()
    {
        var checker = await StartAsync(null);
        var told = 0;
        checker.Checked += () => told++;
        Assert.Throws<UpdateRefusedException>(() => checker.VerifyManifest("{}"u8.ToArray(), "{}", "v1.6.0"));
        Assert.Equal(1, told);
    }
}
