using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Analytics;

namespace AlUmmahNow.Studio.Shell;

/// <summary>
/// Which Studio pages exist for this person (owner rule 2026-11-16, "Hidden when off"). A page whose feature is
/// off, unconfigured or outside the creator tier is not in the navigation at all: no disabled tab, no explanation.
/// Upload follows GET /api/me/features <c>upload</c>; Analytics hides when the API answers 404 <c>feature_off</c> or 402.
/// The platform windows apply <see cref="IsVisible"/> to each tab and show <see cref="Current"/>.
/// </summary>
public sealed class StudioPages
{
    public const string Uploads = "uploads";
    public const string Videos = "videos";
    public const string Analytics = "analytics";
    public const string Encode = "encode";
    public const string Timeline = "timeline";

    /// <summary>Studio settings (Light/Dark, language, accounts, passkeys, watched folder): always there.</summary>
    public const string Settings = "settings";

    /// <summary>Navigation order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Uploads, Videos, Analytics, Encode, Timeline, Settings };

    private readonly StudioShellViewModel _shell;
    private readonly AnalyticsViewModel _analytics;

    public StudioPages(StudioShellViewModel shell, AnalyticsViewModel analytics, string requested = Uploads)
    {
        _shell = shell;
        _analytics = analytics;
        Wanted = All.Contains(requested) ? requested : Uploads;
        Current = Resolve(Wanted);
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StudioShellViewModel.Features)) Update();
        };
        _analytics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalyticsViewModel.IsHidden)) Update();
        };
    }

    /// <summary>The page last chosen (startup tab, a click). It comes back when it reappears.</summary>
    public string Wanted { get; private set; }

    /// <summary>The page on screen: <see cref="Wanted"/>, or the first visible page while it is hidden.</summary>
    public string Current { get; private set; }

    /// <summary>Raised on the UI thread when a page appears or disappears, or <see cref="Current"/> moves.</summary>
    public event EventHandler? Changed;

    public bool IsVisible(string page) => page switch
    {
        Uploads => _shell.Features.IsOn(UserFeatures.Upload),
        Analytics => !_analytics.IsHidden,
        _ => true,
    };

    /// <summary><paramref name="requested"/> when it is visible, else the first visible page (Videos is always there).</summary>
    public string Resolve(string requested) => IsVisible(requested) ? requested : All.First(IsVisible);

    /// <summary>The person (or automation) opened <paramref name="page"/>.</summary>
    public void Select(string page)
    {
        if (!All.Contains(page)) return;
        Wanted = page;
        Update();
    }

    private void Update()
    {
        Current = Resolve(Wanted);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
