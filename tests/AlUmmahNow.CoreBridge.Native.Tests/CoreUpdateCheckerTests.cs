using System.Text.Json;
using AlUmmahNow.CoreBridge.Native;
using Xunit;

namespace AlUmmahNow.CoreBridge.Native.Tests;

/// <summary>The contract of GET /api/v1/app-config (wiki AUN-APP-UPDATES §4.1, §4.1.1), through the real core.</summary>
public sealed class CoreUpdateCheckerTests
{
    private static readonly string[] NoKeys = Array.Empty<string>();

    private static string Answer(string status, string action, string? latest, string minimum = "0.0.0", bool? signed = true, bool? available = true, bool enabled = true, object? notes = null, object? message = null, int? hours = 6) =>
        JsonSerializer.Serialize(new
        {
            api = new { current = "v1" },
            update = new
            {
                enabled, status, minimumVersion = minimum, latestVersion = latest, storeUrl = "https://evil.example/store", channel = "stable", publishedAt = "2026-11-24T20:29:30Z",
                notes, message, action, url = "https://evil.example/x", manifestUrl = "https://evil.example/m", tag = "v9", signed, available, checkHours = hours, keys = (object?)null,
            },
            flags = new { },
            config = new { },
        });

    private static CoreUpdateChecker Checker(string json, IReadOnlyList<string>? keys = null, string version = "1.4.2", string? state = null) =>
        new(_ => Task.FromResult(json), version, state, keys ?? CoreUpdateChecker.RootKeys);

    [Theory]
    [InlineData("install")]
    [InlineData("download")]
    public async Task A_signed_release_is_told_of(string action)
    {
        var answer = await Checker(Answer("recommended", action, "1.5.0", notes: new { en = "new", ar = "جديد" }, hours: 12)).CheckAsync();
        Assert.True(answer.Enabled);
        Assert.Equal(UpdatePromptKind.Notice, answer.Kind);
        Assert.Equal("1.5.0", answer.Version);
        Assert.Equal("new", answer.NotesEn);
        Assert.Equal("جديد", answer.NotesAr);
        Assert.Equal(12, answer.CheckHours);
    }

    [Fact]
    public async Task Below_the_minimum_with_a_signed_release_is_the_screen()
    {
        var answer = await Checker(Answer("required", "download", "1.5.0", "1.4.8", message: new { en = "m", ar = "ر" })).CheckAsync();
        Assert.Equal(UpdatePromptKind.Forced, answer.Kind);
        Assert.Equal("1.4.8", answer.MinimumVersion);
        Assert.Equal("m", answer.MessageEn);
        Assert.Equal("ر", answer.MessageAr);
    }

    /// <summary>The gate: this build has a root key, so nothing unsigned is told of, and never the screen.</summary>
    [Theory]
    [InlineData("notice", false)]
    [InlineData("notice", true)]
    [InlineData("notice", null)]
    [InlineData("download", false)]
    [InlineData("download", null)]
    [InlineData("install", false)]
    [InlineData("store", true)]
    [InlineData("other", true)]
    public async Task With_a_root_key_an_unsigned_release_is_nothing(string action, bool? signed)
    {
        Assert.Equal(UpdatePromptKind.None, (await Checker(Answer("recommended", action, "1.5.0", signed: signed)).CheckAsync()).Kind);
        Assert.Equal(UpdatePromptKind.RequiredBar, (await Checker(Answer("required", action, "1.5.0", "1.4.8", signed: signed)).CheckAsync()).Kind);
    }

    /// <summary>§4.1.1: a build without one treats the three alike.</summary>
    [Theory]
    [InlineData("notice", true)]
    [InlineData("install", true)]
    [InlineData("download", true)]
    [InlineData("store", false)]
    public async Task Without_a_root_key_the_notice_needs_no_signature(string action, bool told)
    {
        Assert.Equal(told ? UpdatePromptKind.Notice : UpdatePromptKind.None, (await Checker(Answer("recommended", action, "1.5.0", signed: false), NoKeys).CheckAsync()).Kind);
        Assert.Equal(told ? UpdatePromptKind.Forced : UpdatePromptKind.RequiredBar, (await Checker(Answer("required", action, "1.5.0", "1.4.8", signed: false), NoKeys).CheckAsync()).Kind);
    }

    [Fact]
    public async Task Nothing_newer_nothing_known_and_switched_off_are_nothing()
    {
        Assert.Equal(UpdatePromptKind.None, (await Checker(Answer("ok", "download", "1.4.2")).CheckAsync()).Kind);
        Assert.Equal(UpdatePromptKind.None, (await Checker(Answer("recommended", "download", "1.4.1")).CheckAsync()).Kind);
        Assert.Equal(UpdatePromptKind.None, (await Checker(Answer("ok", "none", null, signed: null, available: false)).CheckAsync()).Kind);
        Assert.Equal(UpdatePromptKind.RequiredBar, (await Checker(Answer("required", "none", null, "1.4.8", signed: null, available: false)).CheckAsync()).Kind);
        var off = await Checker(Answer("ok", "none", null, "1.4.8", null, null, enabled: false, hours: null)).CheckAsync();
        Assert.False(off.Enabled);
        Assert.Equal(UpdatePromptKind.None, off.Kind);
        Assert.Equal(6, off.CheckHours);
    }

    [Fact]
    public async Task A_preview_is_older_than_its_release()
    {
        Assert.Equal(UpdatePromptKind.Notice, (await Checker(Answer("recommended", "download", "1.5.0"), version: "1.5.0-preview.3").CheckAsync()).Kind);
        Assert.Equal(UpdatePromptKind.Notice, (await Checker(Answer("recommended", "download", "1.5.0-preview.10"), version: "1.5.0-preview.3").CheckAsync()).Kind);
        Assert.Equal("beta", CoreUpdateChecker.Call("viewer", "linux", "1.5.0-preview.3").Query["channel"]);
        Assert.Equal("stable", CoreUpdateChecker.Call("viewer", "linux", "1.5.0").Query["channel"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"update\":null}")]
    [InlineData("{\"update\":{\"enabled\":\"yes\"}}")]
    [InlineData("{\"update\":{\"enabled\":true,\"notes\":7}}")]
    public async Task An_answer_that_cannot_be_read_is_a_check_that_failed(string json) =>
        await Assert.ThrowsAsync<UpdateCheckFailedException>(() => Checker(json).CheckAsync());

    [Fact]
    public async Task A_version_that_is_none_checks_nothing()
    {
        var checker = Checker(Answer("recommended", "download", "1.5.0"), version: "dev");
        await Assert.ThrowsAsync<UpdateCheckFailedException>(() => checker.CheckAsync());
        Assert.Equal(UpdatePromptKind.None, checker.LastKnown());
        Assert.Equal("", checker.State());
    }

    [Fact]
    public async Task What_was_required_is_known_in_the_next_run()
    {
        var checker = Checker(Answer("required", "download", "1.5.0", "1.4.8"));
        Assert.Equal(UpdatePromptKind.None, checker.LastKnown());
        await checker.CheckAsync();
        Assert.Equal(UpdatePromptKind.RequiredBar, Checker("", state: checker.State()).LastKnown());
        // Updated since: no longer
        Assert.Equal(UpdatePromptKind.None, Checker("", state: checker.State(), version: "1.4.8").LastKnown());
    }

    /// <summary>The key list in the build is the root key's, byte for byte: the core takes it, and takes no other.</summary>
    [Fact]
    public void The_build_carries_the_key_list_of_its_root_key()
    {
        using var state = JsonDocument.Parse(Checker("").State());
        Assert.Equal(1, state.RootElement.GetProperty("serial").GetInt32());
        Assert.Equal("LBJ7UzWy2VFNaQU50qgyaOFgBmCkK0+Cgg9eakeGBCc=", Assert.Single(state.RootElement.GetProperty("keys").EnumerateArray()).GetString());
        Assert.DoesNotContain('\r', CoreUpdateChecker.KeyFile("update-keys.json"));
        Assert.Equal("", Checker("", new[] { "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" }).State());
    }

    [Fact]
    public void The_question_has_no_account_and_no_device()
    {
        var call = CoreUpdateChecker.Call("studio", "windows", "1.4.2");
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/v1/app-config", call.PathTemplate);
        Assert.Equal(new[] { "app", "arch", "channel", "platform", "source", "version" }, call.Query.Keys.Order());
        Assert.Equal("managed", call.Query["source"]);
        Assert.Empty(call.Headers);
        Assert.False(call.AuthRequired);
    }

    /// <summary>Over the core's own client, to a server of the test: the question that is asked, and an answer.</summary>
    [Fact]
    public async Task It_asks_the_server_through_the_core()
    {
        using var listener = new System.Net.HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes(Answer("recommended", "download", "1.5.0"));
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(body);
            context.Response.Close();
            return (context.Request.RawUrl!, context.Request.Headers["Authorization"], context.Request.Headers["X-AUN-Client"]);
        });
        var checker = new CoreUpdateChecker(new Uri($"http://127.0.0.1:{port}/"), "studio", "linux", "1.4.2", "AlUmmahNowStudioLinux/1.4.2", null);
        Assert.Equal(UpdatePromptKind.Notice, (await checker.CheckAsync()).Kind);
        var (url, authorization, client) = await serve;
        Assert.StartsWith("/api/v1/app-config?", url);
        foreach (var part in new[] { "app=studio", "platform=linux", "version=1.4.2", "source=managed", "channel=stable" }) Assert.Contains(part, url);
        Assert.Null(authorization);
        Assert.StartsWith("aun-studio/1.4.2", client);
        listener.Stop();
        await Assert.ThrowsAsync<UpdateCheckFailedException>(() => checker.CheckAsync());
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
