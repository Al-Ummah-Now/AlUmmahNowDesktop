using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Encode;

namespace AlUmmahNow.Studio.Demo;

/// <summary>
/// Canned captions, brand kit, assets, analytics and renditions for CI screenshots (AUN_DEMO=1). Nothing is sent
/// to a server. Never used in a signed-in build.
/// </summary>
public sealed class DemoStudioServices : ICaptionsApi, IInstitutionApi, IInsightsApi, IRenditionsApi
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, CaptionDraft>> _drafts = new();
    private readonly List<StudioAsset> _assets = new()
    {
        new StudioAsset { Id = "asset-logo", Kind = StudioAssetKind.Image, Name = "شعار القناة", ContentType = "image/png", Bytes = 48_210, CreatedAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero) },
        new StudioAsset { Id = "asset-intro", Kind = StudioAssetKind.Video, Name = "Intro 2026", ContentType = "video/mp4", Bytes = 6_480_000, CreatedAt = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero) },
        new StudioAsset { Id = "asset-outro", Kind = StudioAssetKind.Video, Name = "Outro", ContentType = "video/mp4", Bytes = 3_120_000, CreatedAt = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero) },
        new StudioAsset { Id = "asset-bg", Kind = StudioAssetKind.Audio, Name = "نغمة الافتتاح", ContentType = "audio/mpeg", Bytes = 912_000, CreatedAt = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero) },
    };

    private BrandKit _kit = new()
    {
        NameEn = "Al Noor channel",
        NameAr = "قناة النور",
        Primary = "#ffa037",
        Secondary = "#093041",
        TextColor = "#f7f1e8",
        Background = "#093041",
        Logo = "asset-logo",
        Intro = "asset-intro",
        Outro = "asset-outro",
        LowerThirdStyle = LowerThirdStyle.Bar,
        LowerThirdPosition = LowerThirdPosition.Start,
        Watermark = WatermarkCorner.TopEnd,
    };

    public DemoStudioServices()
    {
        var q = CaptionSegment.Quran("{\"type\":\"quran\",\"range\":\"18:1\",\"status\":\"accepted\"}");
        var ar = new[]
        {
            new CaptionCue("c1", 0, 2600, new[] { CaptionSegment.Text("السلام عليكم ورحمة الله وبركاته") }),
            new CaptionCue("c2", 2600, 6100, new[] { CaptionSegment.Text("نبدأ اليوم مع أول آية من سورة الكهف") }),
            new CaptionCue("c3", 6100, 9800, new[] { q }),
            new CaptionCue("c4", 9800, 13400, new[] { CaptionSegment.Text("وفيها حمد الله على إنزال الكتاب") }),
            new CaptionCue("c5", 13400, 17000, new[] { CaptionSegment.Text("ثم نتأمل معنى قوله: ولم يجعل له عوجا") }),
            new CaptionCue("c6", 17000, 20500, new[] { CaptionSegment.Text("شاركونا أسئلتكم في التعليقات") }),
        };
        var en = new[]
        {
            new CaptionCue("e1", 0, 2600, new[] { CaptionSegment.Text("Peace be upon you all.") }),
            new CaptionCue("e2", 2600, 6100, new[] { CaptionSegment.Text("Today we start with the first verse of Surat al-Kahf.") }),
        };
        _drafts["demo-v1"] = new Dictionary<string, CaptionDraft>
        {
            ["ar"] = new(new CaptionDraftSummary { Language = "ar", Version = 3, Origin = CaptionOrigin.OnDevice, Edited = true, CueCount = ar.Length, PublishedVersion = 2, UpdatedAt = new DateTimeOffset(2026, 11, 13, 18, 0, 0, TimeSpan.Zero), Track = CaptionTrack.Machine }, ar),
            ["en"] = new(new CaptionDraftSummary { Language = "en", Version = 1, Origin = CaptionOrigin.Transcription, CueCount = en.Length, UpdatedAt = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.Zero) }, en),
        };
    }

    // Captions

    public Task<CaptionDraftList> DraftsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var drafts = _drafts.TryGetValue(videoId, out var d) ? d.Values.Select(x => x.Summary).ToList() : new List<CaptionDraftSummary>();
            return Task.FromResult(new CaptionDraftList { Drafts = drafts, TranscriptAvailable = false });
        }
    }

    public Task<CaptionDraft> DraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _drafts.TryGetValue(videoId, out var d) && d.TryGetValue(language, out var draft)
                ? Task.FromResult(draft)
                : throw StudioApiException.FromServer("draft_not_found", 404, "draft_not_found");
        }
    }

    public Task<CaptionDraftSummary> SaveDraftAsync(string videoId, string language, int baseVersion, IReadOnlyList<CaptionCue> cues, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var old = _drafts[videoId][language].Summary;
            var summary = new CaptionDraftSummary { Language = language, Version = old.Version + 1, Origin = old.Origin, Edited = true, CueCount = cues.Count, PublishedVersion = old.PublishedVersion, UpdatedAt = DateTimeOffset.UtcNow, Track = old.Track };
            _drafts[videoId][language] = new CaptionDraft(summary, CueEditing.ForSaving(cues));
            return Task.FromResult(summary);
        }
    }

    public Task DeleteDraftAsync(string videoId, string language, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_drafts.TryGetValue(videoId, out var d)) d.Remove(language);
            return Task.CompletedTask;
        }
    }

    public Task<CaptionDraftSummary> PublishDraftAsync(string videoId, string language, int baseVersion, bool replaceUpload, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var d = _drafts[videoId][language];
            d.Summary.PublishedVersion = d.Summary.Version;
            d.Summary.PublishedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(d.Summary);
        }
    }

    public Task<IReadOnlyList<CaptionJob>> JobsAsync(string videoId, string? channelId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CaptionJob> jobs = videoId == "demo-v1"
            ? new[] { new CaptionJob { Id = "job-1", Language = "en", Status = CaptionJobStatus.Ready, MinutesReserved = 11, MinutesCharged = 11, CreatedAt = new DateTimeOffset(2026, 11, 12, 17, 40, 0, TimeSpan.Zero) } }
            : Array.Empty<CaptionJob>();
        return Task.FromResult(jobs);
    }

    public Task<CaptionJob> StartJobAsync(string videoId, string language, bool replace, string? channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaptionJob { Id = "job-" + Guid.NewGuid().ToString("N").Substring(0, 6), Language = language, Status = CaptionJobStatus.Queued, MinutesReserved = 11, CreatedAt = DateTimeOffset.UtcNow });

    public Task<CaptionJob> CancelJobAsync(string videoId, string jobId, string? channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaptionJob { Id = jobId, Status = CaptionJobStatus.Cancelled });

    public Task<CaptionMinutes> MinutesAsync(string videoId, string? channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CaptionMinutes { Via = "tier", MonthLimit = 120, MonthUsed = 34, Remaining = 86, MinutesNeeded = 11, ResetsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) });

    public Task<int> SubmitTranscriptAsync(string videoId, string language, OnDeviceTranscript transcript, string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_drafts.TryGetValue(videoId, out var d)) _drafts[videoId] = d = new Dictionary<string, CaptionDraft>();
            if (d.ContainsKey(language) && !transcript.Replace) throw StudioApiException.FromServer("draft_exists", 409, "draft_exists");
            var cues = transcript.Segments.Select((s, i) => new CaptionCue("d" + (i + 1), s.StartMs, s.EndMs, new[] { CaptionSegment.Text(s.Text) })).ToList();
            d[language] = new CaptionDraft(new CaptionDraftSummary { Language = language, Version = 1, Origin = CaptionOrigin.OnDevice, CueCount = cues.Count, UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) }, cues);
            return Task.FromResult(cues.Count);
        }
    }

    // Institution

    public Task<BrandKitState> BrandKitAsync(string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // The second demo channel is managed, not owned: its owner's kit, read-only.
            return Task.FromResult(new BrandKitState(_kit.Clone(), true, channelId == null));
        }
    }

    public Task<BrandKit> SaveBrandKitAsync(BrandKit kit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _kit = kit.Clone();
            return Task.FromResult(_kit.Clone());
        }
    }

    public Task<StudioAssetLibrary> AssetsAsync(string? channelId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new StudioAssetLibrary { Assets = _assets.ToList(), Bytes = _assets.Sum(a => a.Bytes), CanEdit = channelId == null });
        }
    }

    public Task<StudioAsset> AddAssetAsync(string name, byte[] bytes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var asset = new StudioAsset { Id = "asset-" + Guid.NewGuid().ToString("N").Substring(0, 6), Kind = StudioAssetKind.Image, Name = name, ContentType = "image/png", Bytes = bytes.Length, CreatedAt = DateTimeOffset.UtcNow };
            _assets.Insert(0, asset);
            return Task.FromResult(asset);
        }
    }

    public Task<byte[]> AssetDataAsync(string assetId, string? channelId, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

    public Task DeleteAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _assets.RemoveAll(a => a.Id == assetId);
            if (_kit.Logo == assetId) _kit.Logo = null;
            if (_kit.Intro == assetId) _kit.Intro = null;
            if (_kit.Outro == assetId) _kit.Outro = null;
            return Task.CompletedTask;
        }
    }

    // Insights

    public Task<AnalyticsSummary> SummaryAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AnalyticsSummary { TotalViews = 6340, TotalLikes = 268, TotalComments = 41, TotalSubscribers = 312, TotalVideos = 8, AverageViewsPerVideo = 792.5, EngagementRate = 4.9 });

    public Task<IReadOnlyList<DailyMetric>> DailyAsync(string? channelId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromResult(Days(from, to, 1.0));

    public Task<VideoStats> VideoAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var n = int.TryParse(videoId.Replace("demo-v", "", StringComparison.Ordinal), out var i) ? i : 1;
        var daily = Days(from, to, 1.0 / (n + 1));
        return Task.FromResult(new VideoStats
        {
            VideoId = videoId,
            Title = DemoTitles.ElementAtOrDefault(n - 1) ?? videoId,
            Views = 1200 - (n * 130),
            RangeViews = daily.Sum(d => d.Views),
            RangeLikes = 40 - (n * 4),
            RangeComments = 8 - n,
            WatchTimeSeconds = daily.Sum(d => d.WatchTimeSeconds),
            AverageViewSeconds = 260 - (n * 12),
            EngagementRate = 5.6 - (n * 0.3),
            Daily = daily,
        });
    }

    public Task<RetentionCurve> RetentionAsync(string videoId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RetentionCurve(184, Enumerable.Range(0, RetentionCurve.Buckets).Select(b => new RetentionPoint(b, Math.Round(Math.Max(0.18, 1 - (b * 0.035) - (b > 0 ? 0.08 : 0)), 3))).ToList()));

    private static IReadOnlyList<DailyMetric> Days(DateTimeOffset from, DateTimeOffset to, double scale)
    {
        var start = from.UtcDateTime.Date;
        var count = Math.Max(1, (int)(to.UtcDateTime.Date - start).TotalDays + 1);
        return Enumerable.Range(0, count).Select(i => new DailyMetric
        {
            Day = DateTime.SpecifyKind(start.AddDays(i), DateTimeKind.Utc),
            Views = (long)((180 + ((i * 37) % 120)) * scale),
            Likes = (long)((6 + (i % 5)) * scale),
            Comments = (long)((i % 3) * scale),
            Subscribers = (long)((2 + (i % 4)) * scale),
            WatchTimeSeconds = (36000 + ((i * 1700) % 9000)) * scale,
        }).ToList();
    }

    private static readonly string[] DemoTitles =
    {
        "تفسير سورة الكهف — الحلقة الأولى", "Seerah: the early years in Makkah", "أحكام الصيام للمبتدئين", "Arabic grammar, part 3",
        "خطبة الجمعة: الصبر", "Hadith of the week: intentions", "قصص الأنبياء: يونس عليه السلام", "Q&A: prayer while travelling",
    };

    // Renditions

    public Task<RenditionStaging> CreateAsync(string videoId, IReadOnlyList<string> rungs, IReadOnlyList<StagedFile> files, string encoder, string? channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RenditionStaging("demo-staging", "uploading", 100L * 1024 * 1024));

    public Task PutFileAsync(string videoId, string stagingId, StagedFile file, byte[] bytes, string? channelId, CancellationToken cancellationToken = default) => Task.Delay(40, cancellationToken);

    public Task CompleteAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CancelAsync(string videoId, string stagingId, string? channelId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A pretend whisper for screenshots: two models, a short canned transcript. Never used live.</summary>
public sealed class DemoTranscriber : IOnDeviceTranscriber
{
    private readonly HashSet<string> _installed = new() { "base-q5_1" };

    public bool IsAvailable => true;

    public IReadOnlyList<TranscriptionModel> Models() => new[]
    {
        new TranscriptionModel("base-q5_1", _installed.Contains("base-q5_1"), 59_700_000),
        new TranscriptionModel("large-v3-turbo-q5_0", _installed.Contains("large-v3-turbo-q5_0"), 574_000_000),
    };

    public async Task DownloadAsync(string model, IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(100, cancellationToken);
            progress.Report(i / 10.0);
        }

        _installed.Add(model);
    }

    public void DeleteModel(string id) => _installed.Remove(id);

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string model, string mediaPath, string? language, IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(150, cancellationToken);
            progress.Report(i / 10.0);
        }

        return new[] { new TranscriptSegment(0, 2000, "بسم الله"), new TranscriptSegment(2000, 4500, "الحمد لله رب العالمين") };
    }
}

/// <summary>Pretend packaging for screenshots: the server ladder's names and one small file per rung.</summary>
public sealed class DemoRenditionPackaging : IRenditionPackaging, IRungEncoder, IMediaProbe
{
    public IReadOnlyList<HlsRung> Ladder(int width, int height, int rotation) => new[]
    {
        new HlsRung { Name = "360p", Width = 640, Height = 360, Maxrate = 900_000, Bufsize = 1_800_000, AudioBitrate = 96_000 },
        new HlsRung { Name = "720p", Width = 1280, Height = 720, Maxrate = 3_000_000, Bufsize = 6_000_000, AudioBitrate = 128_000 },
    };

    public Task<HlsPackage> PackageAsync(string mp4, string outputDirectory, double segmentSeconds, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "index.m3u8"), "#EXTM3U\n");
        return Task.FromResult(new HlsPackage(new[] { new StagedFile("index.m3u8", 8) }, 1000));
    }

    public IReadOnlyList<StagedFile> Files(string root, IReadOnlyList<string> rungs) =>
        rungs.Select(r => new StagedFile(r + "/index.m3u8", new FileInfo(Path.Combine(root, r, "index.m3u8")).Length)).ToList();

    public async Task EncodeAsync(string source, string output, HlsRung rung, MediaSourceInfo info, IProgress<double> progress, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= 5; i++)
        {
            await Task.Delay(200, cancellationToken);
            progress.Report(i / 5.0);
        }

        File.WriteAllBytes(output, new byte[] { 0 });
    }

    public Task<MediaSourceInfo> ProbeAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(new MediaSourceInfo { Width = 1280, Height = 720, Fps = 30, HasVideo = true, HasAudio = true, DurationMs = 600_000 });
}
