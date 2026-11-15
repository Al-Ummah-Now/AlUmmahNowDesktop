using Xunit;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;
using AlUmmahNow.Studio.Captions;
using AlUmmahNow.Studio.Demo;
using AlUmmahNow.Studio.Encode;
using AlUmmahNow.Studio.Engines;
using AlUmmahNow.Studio.Institution;
using AlUmmahNow.Studio.Shell;
using AlUmmahNow.Studio.Upload;

namespace AlUmmahNow.Studio.Tests;

/// <summary>Demo services with one-shot failures and a record of what was sent.</summary>
public sealed class ScriptedServices : ICaptionsApi, IInstitutionApi, IInsightsApi
{
    private readonly DemoStudioServices _demo = new();
    private readonly Dictionary<string, Queue<Exception>> _fail = new();
    public readonly List<string> Calls = new();
    public readonly List<OnDeviceTranscript> Transcripts = new();
    public readonly List<(bool Replace, string? Channel)> Publishes = new();
    public readonly List<string?> Channels = new();
    public Func<int, CaptionDraftList>? DraftsOverride;
    private int _draftCalls;

    public void Fail(string method, Exception ex)
    {
        if (!_fail.TryGetValue(method, out var q)) _fail[method] = q = new Queue<Exception>();
        q.Enqueue(ex);
    }

    private void Hit(string method, string? channel = null)
    {
        lock (Calls)
        {
            Calls.Add(method);
            Channels.Add(channel);
        }

        if (_fail.TryGetValue(method, out var q) && q.Count > 0) throw q.Dequeue();
    }

    public int Count(string method)
    {
        lock (Calls) return Calls.Count(c => c == method);
    }

    public Task<CaptionDraftList> DraftsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("drafts", channelId);
        var n = Interlocked.Increment(ref _draftCalls);
        return DraftsOverride != null ? Task.FromResult(DraftsOverride(n)) : _demo.DraftsAsync(videoId, channelId, cancellationToken);
    }

    public Task<CaptionDraft> DraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("draft", channelId);
        return _demo.DraftAsync(videoId, language, channelId, cancellationToken);
    }

    public Task<CaptionDraftSummary> SaveDraftAsync(string videoId, string language, int baseVersion, IReadOnlyList<CaptionCue> cues, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("save", channelId);
        return _demo.SaveDraftAsync(videoId, language, baseVersion, cues, channelId, cancellationToken);
    }

    public Task DeleteDraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("deleteDraft", channelId);
        return _demo.DeleteDraftAsync(videoId, language, channelId, cancellationToken);
    }

    public Task<CaptionDraftSummary> PublishDraftAsync(string videoId, string language, int baseVersion, bool replaceUpload, string? channelId, CancellationToken cancellationToken = default)
    {
        Publishes.Add((replaceUpload, channelId));
        Hit("publish", channelId);
        return _demo.PublishDraftAsync(videoId, language, baseVersion, replaceUpload, channelId, cancellationToken);
    }

    public Task<IReadOnlyList<CaptionJob>> JobsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("jobs", channelId);
        return _demo.JobsAsync(videoId, channelId, cancellationToken);
    }

    public Task<CaptionJob> StartJobAsync(string videoId, string language, bool replace, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit(replace ? "startJob:replace" : "startJob", channelId);
        return _demo.StartJobAsync(videoId, language, replace, channelId, cancellationToken);
    }

    public Task<CaptionJob> CancelJobAsync(string videoId, string jobId, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("cancelJob", channelId);
        return _demo.CancelJobAsync(videoId, jobId, channelId, cancellationToken);
    }

    public Task<CaptionMinutes> MinutesAsync(string videoId, string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("minutes", channelId);
        return _demo.MinutesAsync(videoId, channelId, cancellationToken);
    }

    public Task<int> SubmitTranscriptAsync(string videoId, string language, OnDeviceTranscript transcript, string? channelId, CancellationToken cancellationToken = default)
    {
        Transcripts.Add(transcript);
        Hit("transcript", channelId);
        return _demo.SubmitTranscriptAsync(videoId, language, transcript, channelId, cancellationToken);
    }

    public Task<BrandKitState> BrandKitAsync(string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("kit", channelId);
        return _demo.BrandKitAsync(channelId, cancellationToken);
    }

    public Task<BrandKit> SaveBrandKitAsync(BrandKit kit, CancellationToken cancellationToken = default)
    {
        Hit("saveKit");
        return _demo.SaveBrandKitAsync(kit, cancellationToken);
    }

    public Task<StudioAssetLibrary> AssetsAsync(string? channelId, CancellationToken cancellationToken = default)
    {
        Hit("assets", channelId);
        return _demo.AssetsAsync(channelId, cancellationToken);
    }

    public Task<StudioAsset> AddAssetAsync(string name, byte[] bytes, CancellationToken cancellationToken = default)
    {
        Hit("addAsset");
        return _demo.AddAssetAsync(name, bytes, cancellationToken);
    }

    public Task<byte[]> AssetDataAsync(string assetId, string? channelId, CancellationToken cancellationToken = default) => _demo.AssetDataAsync(assetId, channelId, cancellationToken);

    public Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        Hit("deleteAsset");
        return _demo.DeleteAssetAsync(assetId, cancellationToken);
    }

    public Task<AnalyticsSummary> SummaryAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        Hit("summary", channelId);
        return _demo.SummaryAsync(channelId, from, to, cancellationToken);
    }

    public Task<IReadOnlyList<DailyMetric>> DailyAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        Hit("daily", channelId);
        return _demo.DailyAsync(channelId, from, to, cancellationToken);
    }

    public Task<VideoStats> VideoAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        Hit("video:" + videoId);
        return _demo.VideoAsync(videoId, from, to, cancellationToken);
    }

    public Task<RetentionCurve> RetentionAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        Hit("retention");
        return _demo.RetentionAsync(videoId, from, to, cancellationToken);
    }
}

internal static class ScreensSetup
{
    public static async Task<(StudioShellViewModel Shell, StudioApiClient Api, FakeTransport T)> ShellAsync(string role = "owner")
    {
        var accounts = new FakeAccounts();
        accounts.Saved.Add(new StudioAccount("u1", null, "A", null, null, false));
        accounts.ActiveId = "u1";
        var t = new FakeTransport();
        t.Responses["get_ChannelAccessList"] = _ => FakeTransport.Json(new { data = new[] { new { id = "c1", userId = "u1", name = "C", isMain = true, role, capabilities = Array.Empty<string>() } } });
        t.Responses["get_VideosList"] = _ => FakeTransport.Json(new { data = new object[] { new { id = "demo-v1", title = "One", channelId = "c1", isPublic = true }, new { id = "demo-v2", title = "Two", channelId = "c1", isPublic = true } } });
        t.Responses["get_AnalyticsChannelGet"] = _ => FakeTransport.Json(new
        {
            recentMetrics = new[] { new { date = "2026-11-01", views = 4, watchTime = 60 } },
            topPerformingVideos = new[] { new { videoId = "demo-v1", title = "One", views = 10 }, new { videoId = "demo-v2", title = "Two", views = 9 } },
        });
        var api = new StudioApiClient(t);
        var shell = new StudioShellViewModel(accounts, api);
        await shell.InitializeAsync();
        return (shell, api, t);
    }

    public static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

public sealed class CaptionsViewModelTests
{
    private static async Task<(CaptionsViewModel Vm, ScriptedServices S)> CreateAsync(IOnDeviceTranscriber? transcriber = null)
    {
        var (shell, api, _) = await ScreensSetup.ShellAsync();
        var s = new ScriptedServices();
        var vm = new CaptionsViewModel(shell, api, s, transcriber) { Pause = (_, _) => Task.CompletedTask };
        await vm.LoadVideosAsync();
        await ScreensSetup.Until(() => vm.Drafts != null && !vm.IsLoading);
        return (vm, s);
    }

    [Fact]
    public async Task LoadsDraftsJobsAndMinutesOfTheFirstVideo()
    {
        var (vm, s) = await CreateAsync();
        Assert.Equal("demo-v1", vm.Video!.Id);
        Assert.Equal(new[] { "ar", "en" }, vm.Drafts!.Drafts.Select(d => d.Language).OrderBy(x => x));
        Assert.Equal(CaptionOrigin.OnDevice, vm.Drafts.Drafts.Single(d => d.Language == "ar").Origin);
        Assert.Single(vm.Jobs);
        Assert.Equal(86, vm.Minutes!.Remaining);
        Assert.All(s.Channels.Where(c => c != null), c => Assert.Equal("c1", c));
    }

    [Fact]
    public async Task ServerJobAsksBeforeReplacingAnExistingDraft()
    {
        var (vm, s) = await CreateAsync();
        var asked = new List<string>();
        vm.Confirm = (key, _) =>
        {
            asked.Add(key);
            return Task.FromResult(false);
        };
        vm.Language = "ar";
        await vm.StartServerJobAsync();
        Assert.Equal(new[] { "studio.captions.replaceDraft" }, asked);
        Assert.Equal(0, s.Count("startJob") + s.Count("startJob:replace"));

        vm.Confirm = (_, _) => Task.FromResult(true);
        await vm.StartServerJobAsync();
        Assert.Equal(1, s.Count("startJob:replace"));
        Assert.Equal("studio.captions.jobStarted", vm.NoticeKey);
        Assert.Equal(2, vm.Jobs.Count);
    }

    [Fact]
    public async Task OutOfMinutesShowsTheMinutesMessage()
    {
        var (vm, s) = await CreateAsync();
        await vm.DeleteDraftForTest(s, "ar");
        s.Fail("startJob", StudioApiException.FromServer("upgrade_required", 402, "upgrade_required"));
        vm.Language = "ar";
        await vm.StartServerJobAsync();
        Assert.Equal("studio.captions.noMinutes", vm.ErrorKey);
    }

    [Fact]
    public async Task ForbiddenLoadSaysTheRoleCannotChangeCaptions()
    {
        var (vm, s) = await CreateAsync();
        s.Fail("drafts", StudioApiException.FromServer("forbidden", 403, "forbidden"));
        await vm.LoadAsync();
        Assert.Equal("studio.captions.notAllowed", vm.ErrorKey);
    }

    [Fact]
    public async Task OnDeviceDownloadsTheModelTranscribesSendsAndWaitsForTheDraft()
    {
        var transcriber = new FakeTranscriber();
        var (vm, s) = await CreateAsync(transcriber);
        vm.RefreshModels();
        vm.Model = "large";
        vm.Language = "en";
        vm.Confirm = (_, _) => Task.FromResult(true);
        var stages = new List<OnDeviceStage>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Stage)) stages.Add(vm.Stage);
        };
        await vm.TranscribeOnDeviceAsync("/videos/one.mp4");
        Assert.Equal(new[] { OnDeviceStage.Downloading, OnDeviceStage.Transcribing, OnDeviceStage.Sending, OnDeviceStage.Waiting, OnDeviceStage.Done }, stages);
        Assert.Equal("large", transcriber.Downloaded.Single());
        var sent = s.Transcripts.Single();
        Assert.True(sent.Replace);
        Assert.Equal("whisper.cpp", OnDeviceTranscript.Provider);
        Assert.Equal("large", sent.Model);
        Assert.Equal("studio.captions.device.ready", vm.NoticeKey);
        Assert.Equal(CaptionOrigin.OnDevice, vm.Drafts!.Drafts.Single(d => d.Language == "en").Origin);
    }

    [Fact]
    public async Task OnDeviceWithNoSpeechSendsNothing()
    {
        var transcriber = new FakeTranscriber { Segments = Array.Empty<TranscriptSegment>() };
        var (vm, s) = await CreateAsync(transcriber);
        vm.RefreshModels();
        vm.Language = "en";
        vm.Confirm = (_, _) => Task.FromResult(true);
        await vm.TranscribeOnDeviceAsync("/videos/one.mp4");
        Assert.Equal(OnDeviceStage.Failed, vm.Stage);
        Assert.Equal("studio.captions.device.noSpeech", vm.ErrorKey);
        Assert.Empty(s.Transcripts);
    }

    [Fact]
    public async Task FailedClaimIsReported()
    {
        var transcriber = new FakeTranscriber();
        var (vm, s) = await CreateAsync(transcriber);
        vm.RefreshModels();
        vm.Language = "en";
        vm.Confirm = (_, _) => Task.FromResult(true);
        s.DraftsOverride = _ => new CaptionDraftList { Claims = new[] { new CaptionClaim("en", CaptionClaimStatus.Failed, "bad") } };
        await vm.TranscribeOnDeviceAsync("/videos/one.mp4");
        Assert.Equal(OnDeviceStage.Failed, vm.Stage);
        Assert.Equal("studio.captions.device.claimFailed", vm.ErrorKey);
    }

    [Fact]
    public async Task OpensTheEditorAndClosingReloads()
    {
        var (vm, s) = await CreateAsync();
        await vm.OpenDraftAsync("ar");
        Assert.NotNull(vm.Editor);
        Assert.True(vm.Editor!.IsRtl);
        var before = s.Count("drafts");
        await vm.Editor.CloseAsync();
        Assert.Null(vm.Editor);
        await ScreensSetup.Until(() => s.Count("drafts") > before);
    }

    private sealed class FakeTranscriber : IOnDeviceTranscriber
    {
        private readonly HashSet<string> _installed = new() { "base" };
        public readonly List<string> Downloaded = new();
        public IReadOnlyList<TranscriptSegment> Segments = new[] { new TranscriptSegment(0, 1500, "Hello"), new TranscriptSegment(1500, 3000, "everyone") };

        public bool IsAvailable => true;

        public IReadOnlyList<TranscriptionModel> Models() => new[] { new TranscriptionModel("base", _installed.Contains("base"), 1), new TranscriptionModel("large", _installed.Contains("large"), 2) };

        public Task DownloadAsync(string model, IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress.Report(1);
            Downloaded.Add(model);
            _installed.Add(model);
            return Task.CompletedTask;
        }

        public void DeleteModel(string id) => _installed.Remove(id);

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string model, string mediaPath, string? language, IProgress<double> progress, CancellationToken cancellationToken)
        {
            progress.Report(1);
            return Task.FromResult(Segments);
        }
    }
}

internal static class CaptionsTestExtensions
{
    public static async Task DeleteDraftForTest(this CaptionsViewModel vm, ScriptedServices s, string language)
    {
        await s.DeleteDraftAsync(vm.Video!.Id, language, null);
        await vm.LoadAsync();
    }
}

public sealed class CueEditorViewModelTests
{
    private static async Task<(CueEditorViewModel Vm, ScriptedServices S)> CreateAsync(string language = "ar")
    {
        var s = new ScriptedServices();
        var draft = await s.DraftAsync("demo-v1", language, "c1");
        return (new CueEditorViewModel(s, "demo-v1", "c1", draft), s);
    }

    [Fact]
    public async Task TypingInOneSegmentIsOneUndoStep()
    {
        var (vm, _) = await CreateAsync();
        var original = vm.Cues[0].Segments[0].Speech;
        vm.SetText(0, 0, "a");
        vm.SetText(0, 0, "ab");
        vm.SetText(0, 0, "abc");
        Assert.True(vm.IsDirty);
        vm.Undo();
        Assert.Equal(original, vm.Cues[0].Segments[0].Speech);
        Assert.False(vm.IsDirty);
        vm.Redo();
        Assert.Equal("abc", vm.Cues[0].Segments[0].Speech);
    }

    [Fact]
    public async Task OverlappingTimesAreRefused()
    {
        var (vm, _) = await CreateAsync();
        Assert.False(vm.SetTimes(1, 1000, 5000));
        Assert.Equal("studio.captions.editor.badTimes", vm.ErrorKey);
        Assert.False(vm.IsDirty);
        Assert.True(vm.SetTimes(1, 2700, 6000));
        Assert.Null(vm.ErrorKey);
    }

    [Fact]
    public async Task SplitMergeAndDeleteKeepASelection()
    {
        var (vm, _) = await CreateAsync();
        var count = vm.Cues.Count;
        Assert.True(vm.Split(0, 0, 6));
        Assert.Equal(count + 1, vm.Cues.Count);
        Assert.True(vm.MergeWithNext(0));
        Assert.Equal(count, vm.Cues.Count);
        vm.Delete(count - 1);
        Assert.Equal(count - 1, vm.Cues.Count);
        Assert.Equal(count - 2, vm.Selected);
    }

    [Fact]
    public async Task SaveSendsTheChannelAndClearsDirty()
    {
        var (vm, s) = await CreateAsync();
        vm.SetText(0, 0, "مرحبًا");
        Assert.True(await vm.SaveAsync());
        Assert.False(vm.IsDirty);
        Assert.Equal(4, vm.Summary.Version);
        Assert.Equal("c1", s.Channels[s.Calls.LastIndexOf("save")]);
        Assert.Equal("studio.captions.editor.saved", vm.NoticeKey);
    }

    [Fact]
    public async Task ConflictTellsTheCreatorToReopen()
    {
        var (vm, s) = await CreateAsync();
        vm.SetText(0, 0, "x");
        s.Fail("save", StudioApiException.FromServer("draft_conflict", 409, "draft_conflict"));
        Assert.False(await vm.SaveAsync());
        Assert.Equal("studio.captions.editor.conflict", vm.ErrorKey);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task PublishAsksBeforeReplacingAnUploadedTrack()
    {
        var (vm, s) = await CreateAsync();
        s.Fail("publish", StudioApiException.FromServer("replaces_upload", 409, "replaces_upload"));
        vm.Confirm = (key, _) => Task.FromResult(key == "studio.captions.editor.replacesUpload");
        Assert.True(await vm.PublishAsync());
        Assert.Equal(new[] { false, true }, s.Publishes.Select(p => p.Replace));
        Assert.Equal("studio.captions.editor.published", vm.NoticeKey);
    }

    [Fact]
    public async Task ClosingWithChangesAsksAndDeletingClosesAfterConfirm()
    {
        var (vm, s) = await CreateAsync();
        var closed = 0;
        vm.Closed += (_, _) => closed++;
        vm.SetText(0, 0, "x");
        await vm.CloseAsync();
        Assert.Equal(0, closed);
        vm.Confirm = (_, _) => Task.FromResult(true);
        await vm.DeleteDraftAsync();
        Assert.Equal(1, closed);
        Assert.Equal(1, s.Count("deleteDraft"));
    }

    [Fact]
    public async Task EnglishIsLeftToRight()
    {
        var (vm, _) = await CreateAsync("en");
        Assert.False(vm.IsRtl);
    }

    [Theory]
    [InlineData(0, "0:00.000")]
    [InlineData(61_500, "1:01.500")]
    [InlineData(3_723_004, "1:02:03.004")]
    public void TimeRoundTrips(int ms, string text)
    {
        Assert.Equal(text, CueEditorViewModel.Time(ms));
        Assert.Equal(ms, CueEditorViewModel.ParseTime(text));
    }

    [Fact]
    public void BadTimesDoNotParse()
    {
        Assert.Null(CueEditorViewModel.ParseTime("abc"));
        Assert.Null(CueEditorViewModel.ParseTime(""));
    }
}

public sealed class InsightsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChannelLoadsSummaryDailyAndTopVideosByPeriodViews()
    {
        var (shell, api, _) = await ScreensSetup.ShellAsync();
        var s = new ScriptedServices();
        var vm = new InsightsViewModel(shell, api, s, () => Now);
        await vm.LoadAsync();
        Assert.Equal(6340, vm.Summary!.TotalViews);
        Assert.Equal(28, vm.Daily.Count);
        Assert.Equal(new[] { "demo-v1", "demo-v2" }, vm.VideoStats.Select(v => v.VideoId));
        Assert.Null(vm.Retention);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), vm.Period().From);
    }

    [Fact]
    public async Task VideoScopeLoadsItsStatsAndRetention()
    {
        var (shell, api, _) = await ScreensSetup.ShellAsync();
        var s = new ScriptedServices();
        var vm = new InsightsViewModel(shell, api, s, () => Now);
        vm.VideoId = "demo-v2";
        await vm.LoadAsync();
        Assert.Equal("demo-v2", vm.SelectedVideo!.VideoId);
        Assert.Equal(RetentionCurve.Buckets, vm.Retention!.Points!.Count);
        var csv = vm.ExportRetention(new[] { "pct", "share" });
        Assert.NotNull(csv);
        Assert.EndsWith(".csv", csv!.Value.FileName, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv.Value.Bytes.Take(3));
    }

    [Fact]
    public async Task RebuildingAnalyticsSaysSo()
    {
        var (shell, api, _) = await ScreensSetup.ShellAsync();
        var s = new ScriptedServices();
        s.Fail("summary", StudioApiException.FromServer("rebuilding", 503, "rebuilding"));
        var vm = new InsightsViewModel(shell, api, s, () => Now);
        await vm.LoadAsync();
        Assert.Equal("studio.insights.rebuilding", vm.ErrorKey);
    }

    [Fact]
    public async Task DailyCsvHasIsoDatesAndTheGivenHeader()
    {
        var (shell, api, _) = await ScreensSetup.ShellAsync();
        var vm = new InsightsViewModel(shell, api, new ScriptedServices(), () => Now) { Days = 7 };
        await vm.LoadAsync();
        var (name, bytes) = vm.ExportDaily(new[] { "Date", "Views", "Watch time (minutes)", "Likes", "Comments", "Subscribers" });
        var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Date,Views", text);
        Assert.Contains("2026-11-04", text, StringComparison.Ordinal);
        Assert.Contains("daily", name, StringComparison.Ordinal);
    }
}

public sealed class InstitutionViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static async Task<(InstitutionViewModel Vm, ScriptedServices S)> CreateAsync(string role = "owner")
    {
        var (shell, _, _) = await ScreensSetup.ShellAsync(role);
        var s = new ScriptedServices();
        var vm = new InstitutionViewModel(shell, s);
        await vm.LoadAsync();
        return (vm, s);
    }

    [Fact]
    public async Task OwnerEditsSavesAndReverts()
    {
        var (vm, s) = await CreateAsync();
        Assert.True(vm.CanEdit);
        Assert.Null(s.Channels[s.Calls.IndexOf("kit")]);
        vm.Edit(k => k.Primary = "#112233");
        Assert.True(vm.IsDirty);
        vm.Revert();
        Assert.False(vm.IsDirty);
        vm.Edit(k => k.NameEn = "New name");
        await vm.SaveAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal("New name", vm.State!.Kit.NameEn);
        Assert.Equal("studio.brand.saved", vm.NoticeKey);
    }

    [Fact]
    public async Task InvalidColourIsCaughtBeforeSending()
    {
        var (vm, s) = await CreateAsync();
        vm.Edit(k => k.TextColor = "red");
        await vm.SaveAsync();
        Assert.Equal("studio.brand.invalid.colors_text", vm.ErrorKey);
        Assert.Equal(0, s.Count("saveKit"));
    }

    [Fact]
    public async Task TeamMembersSeeTheOwnersKitReadOnly()
    {
        var (vm, s) = await CreateAsync("editor");
        Assert.Equal("c1", s.Channels[s.Calls.IndexOf("kit")]);
        Assert.False(vm.CanEdit);
        vm.Edit(k => k.Primary = "#000000");
        Assert.False(vm.IsDirty);
        await vm.DeleteAssetAsync(vm.Library!.Assets[0]);
        Assert.Equal(0, s.Count("deleteAsset"));
    }

    [Fact]
    public async Task DeletingAKitAssetWarnsAndReloadsTheKit()
    {
        var (vm, s) = await CreateAsync();
        var asked = new List<string>();
        vm.Confirm = (key, _) =>
        {
            asked.Add(key);
            return Task.FromResult(true);
        };
        await vm.DeleteAssetAsync(vm.Library!.Assets.Single(a => a.Id == "asset-logo"));
        await vm.DeleteAssetAsync(vm.Library!.Assets.Single(a => a.Id == "asset-bg"));
        Assert.Equal(new[] { "studio.assets.deleteUsed", "studio.assets.deleteConfirm" }, asked);
        Assert.Null(vm.Kit.Logo);
        Assert.DoesNotContain(vm.Library.Assets, a => a.Id is "asset-logo" or "asset-bg");
    }

    [Fact]
    public async Task AddsAFileAndMapsServerRefusals()
    {
        var (vm, s) = await CreateAsync();
        var path = Path.Combine(_dir, "logo.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        await vm.AddAssetAsync(path);
        Assert.Equal("studio.assets.added", vm.NoticeKey);
        Assert.Contains(vm.Library!.Assets, a => a.Name == "logo");

        s.Fail("addAsset", StudioApiException.FromServer("unsupported_type", 415, "unsupported_type"));
        await vm.AddAssetAsync(path);
        Assert.Equal("studio.assets.unsupported", vm.ErrorKey);

        var empty = Path.Combine(_dir, "empty.png");
        await File.WriteAllBytesAsync(empty, Array.Empty<byte>());
        var before = s.Count("addAsset");
        await vm.AddAssetAsync(empty);
        Assert.Equal("studio.assets.empty", vm.ErrorKey);
        Assert.Equal(before, s.Count("addAsset"));
    }
}

public sealed class PreEncodeCoordinatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static UploadSnapshot Snap(string id, UploadPhase phase, string? videoId) => new(id, "/v.mp4", "V", phase, 0, 100, videoId, null);

    private PreEncodeCoordinator Create(DemoRenditionPackaging media, IRenditionsApi api)
    {
        return new PreEncodeCoordinator(() => new LocalRenditionJob(media, media, media, api)
        {
            ConfigureUploader = u =>
            {
                u.Read = _ => new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };
                u.Pause = (_, _) => Task.CompletedTask;
            },
        }, _dir);
    }

    [Fact]
    public async Task StartsOnceTheVideoExistsAndCleansUp()
    {
        var c = Create(new DemoRenditionPackaging(), new DemoStudioServices());
        c.Want("u1", "/v.mp4", "c1");
        c.Observe(new[] { Snap("u1", UploadPhase.Starting, null) });
        Assert.Null(c.StatusOf("u1"));
        c.Observe(new[] { Snap("u1", UploadPhase.Uploading, "vid1") });
        Assert.NotNull(c.StatusOf("u1"));
        await ScreensSetup.Until(() => c.StatusOf("u1")?.Outcome != null);
        Assert.Equal(RenditionOutcomeKind.Ready, c.StatusOf("u1")!.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_dir, "u1")));

        // Not run twice
        c.Observe(new[] { Snap("u1", UploadPhase.Uploading, "vid1") });
        Assert.Equal(RenditionOutcomeKind.Ready, c.StatusOf("u1")!.Outcome);
    }

    [Fact]
    public void UploadsNotWantedAreIgnored()
    {
        var c = Create(new DemoRenditionPackaging(), new DemoStudioServices());
        c.Observe(new[] { Snap("u2", UploadPhase.Uploading, "vid2") });
        Assert.Null(c.StatusOf("u2"));
    }

    [Fact]
    public async Task CancellingTheUploadCancelsThePreEncode()
    {
        var c = Create(new DemoRenditionPackaging(), new DemoStudioServices());
        c.Want("u3", "/v.mp4", "c1");
        c.Observe(new[] { Snap("u3", UploadPhase.Uploading, "vid3") });
        c.Observe(new[] { Snap("u3", UploadPhase.Cancelled, "vid3") });
        await ScreensSetup.Until(() => c.StatusOf("u3")?.Outcome != null);
        Assert.Equal(RenditionOutcomeKind.Cancelled, c.StatusOf("u3")!.Outcome);
    }
}
