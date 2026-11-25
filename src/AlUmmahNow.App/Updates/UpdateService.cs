using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.CoreBridge;
using AlUmmahNow.CoreBridge.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlUmmahNow.App.Updates
{
    public enum UpdateStage
    {
        Hidden,

        /// <summary>A bar: there is a new version. It closes for that version.</summary>
        Available,

        /// <summary>A bar that stays: too old, and nothing to update to that the app knows of.</summary>
        Required,

        /// <summary>A bar that stays: too old, but work is running that the screen must not interrupt.</summary>
        Waiting,

        /// <summary>The whole window: too old.</summary>
        Forced,
    }

    /// <summary>What the last check came to, for Settings → Updates.</summary>
    public enum UpdateResult { None, Checking, UpToDate, Found, Required, Failed }

    /// <summary>How far an update is that is installed in place.</summary>
    public enum UpdateStep
    {
        None,
        Downloading,

        /// <summary>Downloaded and checked: installed when the app closes, or restarts.</summary>
        Ready,

        /// <summary>It could not be downloaded; it can be tried again.</summary>
        Failed,
    }

    /// <summary>Everything the views show of an update. Replaced, never changed.</summary>
    public sealed class UpdateView
    {
        public static readonly UpdateView None = new UpdateView(UpdateStage.Hidden, UpdateAnswer.Off);

        internal UpdateView(UpdateStage stage, UpdateAnswer answer, bool installs = false, UpdateStep step = UpdateStep.None, double progress = 0, bool isBusy = false, bool isOpenElsewhere = false)
        {
            Stage = stage;
            Installs = installs;
            Step = step;
            Progress = progress;
            IsBusy = isBusy || isOpenElsewhere;
            IsOpenElsewhere = isOpenElsewhere;
            Version = UpdateService.CleanVersion(answer.Version);
            MinimumVersion = UpdateService.CleanVersion(answer.MinimumVersion);
            NotesEn = UpdateService.CleanText(answer.NotesEn);
            NotesAr = UpdateService.CleanText(answer.NotesAr);
            MessageEn = UpdateService.CleanText(answer.MessageEn);
            MessageAr = UpdateService.CleanText(answer.MessageAr);
        }

        public UpdateStage Stage { get; }
        public string? Version { get; }
        public string? MinimumVersion { get; }
        public string NotesEn { get; }
        public string NotesAr { get; }
        public string MessageEn { get; }
        public string MessageAr { get; }

        /// <summary>The app installs it itself: the button updates, and does not open the download page.</summary>
        public bool Installs { get; }

        public UpdateStep Step { get; }

        /// <summary>Of the download, 0 to 1.</summary>
        public double Progress { get; }

        /// <summary>Work is running that a restart would interrupt.</summary>
        public bool IsBusy { get; }

        /// <summary>The app is open a second time: the updater would end that one too.</summary>
        public bool IsOpenElsewhere { get; }

        /// <summary>The button does something now.</summary>
        public bool CanBegin => Step != UpdateStep.Downloading && !(Step == UpdateStep.Ready && IsBusy);

        public bool IsBar => Stage == UpdateStage.Available || Stage == UpdateStage.Required || Stage == UpdateStage.Waiting;
        public bool CanClose => Stage == UpdateStage.Available && Step != UpdateStep.Downloading;

        /// <summary>There is a version to download.</summary>
        public bool CanDownload => Stage == UpdateStage.Available || Stage == UpdateStage.Waiting || Stage == UpdateStage.Forced;
    }

    /// <summary>
    /// The app's check for a new version (wiki AUN-APP-UPDATES): about 30 s after launch, every <c>checkHours</c>, and on
    /// Check now. Most copies only tell: their one link is compiled in (<see cref="UpdateInstalls.DownloadPage"/>, opened by
    /// <see cref="IUpdateDelivery"/>). A copy of our own setup installs in place (<see cref="ISelfUpdate"/>): downloaded and
    /// checked in the background or when asked, checked again right before it is installed, which is when the app closes or
    /// is restarted, and never while work is running.
    /// Everything runs on the thread that made it, the UI thread.
    /// </summary>
    public sealed class UpdateService : ObservableObject
    {
        public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);

        /// <summary>The longest single wait: a computer that slept is asked for soon after it wakes.</summary>
        public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(10);

        /// <summary>How often running work is looked at while the screen waits for it.</summary>
        public static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(2);

        /// <summary>How long the app's end waits for a refusal to be told.</summary>
        public static readonly TimeSpan ReportLimit = TimeSpan.FromSeconds(2);

        public const int MaxText = 1_500;
        public const int MaxDismissed = 10;

        private static readonly Logger Logger = Log.For("updates");
        private static readonly Regex VersionShape = new Regex(@"\A[0-9]{1,6}(\.[0-9]{1,6}){2,3}(-[0-9A-Za-z.]{1,40})?\z", RegexOptions.CultureInvariant);

        private readonly IUpdateChecker _checker;
        private readonly UpdateStore _store;
        private readonly UpdateMemory _memory;
        private readonly Func<bool> _isBusy;
        private readonly IUpdateDelivery _delivery;
        private readonly ISelfUpdate? _self;
        private readonly Action? _close;
        private bool _isEnding;
        private bool _mayInstall;
        private bool _isHandedOver;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private UpdateAnswer _answer = UpdateAnswer.Off;
        private UpdateView _view = UpdateView.None;
        private UpdateResult _result;
        private Task<bool>? _checking;
        private DateTimeOffset _due;
        private TimeSpan _every = TimeSpan.FromHours(6);
        private CancellationToken _stop;
        private bool _isWaitingForWork;
        private VerifiedUpdate? _ready;
        private Task? _staging;
        /// <summary>The person pressed Update for this release; else it is downloaded in the background.</summary>
        private bool _isAsked;
        private string? _tag;
        private UpdateStep _step;
        private double _progress;

        /// <param name="isBusy">An upload, an encode or a live session is running: the screen waits for it.</param>
        /// <param name="wasEnabled">Review frames: the switch was on in an earlier run.</param>
        /// <param name="self">Installs in place, in a copy of our own setup.</param>
        public UpdateService(IUpdateChecker checker, UpdateStore store, UpdateInstall install, string installedVersion, Func<bool> isBusy, IUpdateDelivery delivery,
            Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, bool wasEnabled = false, ISelfUpdate? self = null, Action? close = null)
        {
            _self = install == UpdateInstall.SelfUpdating ? self : null;
            _checker = checker;
            _store = store;
            _memory = store.Load();
            // What the core learns of a manifest is kept before anything else is done with it
            checker.Checked += Keep;
            _close = close;
            _memory.Enabled |= wasEnabled;
            Install = install;
            InstalledVersion = installedVersion;
            _isBusy = isBusy;
            _delivery = delivery;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _delay = delay ?? Task.Delay;
        }

        public UpdateInstall Install { get; }
        public string InstalledVersion { get; }

        public UpdateView View
        {
            get => _view;
            private set => SetProperty(ref _view, value);
        }

        public UpdateResult Result
        {
            get => _result;
            private set => SetProperty(ref _result, value);
        }

        /// <summary>Settings → Updates is there: the server's switch is on, and this copy is not another store's to update.</summary>
        public bool IsSectionVisible => Install.Checks() && _memory.Enabled;

        public DateTimeOffset? LastChecked => _memory.LastChecked;

        /// <summary>The running or last check was asked for: only then is it told that it came to nothing.</summary>
        public bool IsCheckAsked { get; private set; }

        /// <summary>The version Settings tells of, also when its bar was closed.</summary>
        public UpdateView Found => ViewOf(_view.Stage);

        /// <summary>This copy installs updates itself: Settings has the choice of doing it automatically.</summary>
        public bool CanInstall => _self != null;

        /// <summary>The person's own choice, else the admins' default, else on.</summary>
        public bool InstallsAutomatically
        {
            get => _memory.InstallsAutomatically ?? _memory.InstallsByDefault;
            set
            {
                if (InstallsAutomatically == value) return;
                _memory.InstallsAutomatically = value;
                Keep();
                // Downloaded without being asked: not installed either
                if (!value && !_isAsked) Drop();
                OnPropertyChanged();
                Show();
                BeginQuietly();
            }
        }

        /// <summary>The schedule, for as long as the app runs.</summary>
        public async Task RunAsync(CancellationToken stop = default)
        {
            if (!Install.Checks()) return;
            _stop = stop;
            try
            {
                // Before the first answer, and offline: what is known asks for a bar at most, never for the screen
                if (_memory.Enabled && _checker.LastKnown() is UpdatePromptKind.RequiredBar or UpdatePromptKind.Forced)
                {
                    _answer = new UpdateAnswer { Enabled = true, Kind = UpdatePromptKind.RequiredBar };
                    View = new UpdateView(UpdateStage.Required, _answer);
                }

                _due = _now() + FirstCheck;
                while (true)
                {
                    stop.ThrowIfCancellationRequested();
                    // A clock that was set back must not put the next check off
                    if (_due - _now() > _every) _due = _now() + _every;
                    var wait = _due - _now();
                    if (wait > TimeSpan.Zero) await _delay(wait < LongestWait ? wait : LongestWait, stop);
                    else await CheckAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // The app closes
            }
        }

        /// <summary>One check, or the one that is running. False when it came to nothing (offline).</summary>
        /// <param name="isAsked">Check now: a person waits for what it comes to.</param>
        public Task<bool> CheckAsync(bool isAsked = false)
        {
            if (!Install.Checks()) return Task.FromResult(false);
            var running = _checking is { IsCompleted: false };
            IsCheckAsked = isAsked || running && IsCheckAsked;
            return running ? _checking! : _checking = CheckOnceAsync();
        }

        private async Task<bool> CheckOnceAsync()
        {
            Result = UpdateResult.Checking;
            var ok = false;
            try
            {
                var answer = await _checker.CheckAsync(_stop);
                _every = TimeSpan.FromHours(Math.Min(48, Math.Max(1, answer.CheckHours)));
                _memory.Enabled = answer.Enabled;
                _memory.LastChecked = _now();
                _memory.InstallsByDefault = answer.InstallsByDefault;
                Keep();
                _answer = Told(answer);
                // Too old with nothing to update to is neither: it must not read as the latest version
                Result = IsNew(_answer) ? UpdateResult.Found : _answer.Kind is UpdatePromptKind.RequiredBar or UpdatePromptKind.Forced ? UpdateResult.Required : UpdateResult.UpToDate;
                await LookAgainAsync();
                ok = true;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                Result = UpdateResult.None;
                throw;
            }
#pragma warning disable CA1031 // A check that goes wrong, however it does, is one that came to nothing: never a dead app, never a dead schedule.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Logger.Warn("update check failed", ex);
                Result = UpdateResult.Failed;
            }
            finally
            {
                _due = _now() + _every;
            }

            if (ok)
            {
                Show();
                BeginQuietly();
            }

            OnPropertyChanged(nameof(IsSectionVisible));
            OnPropertyChanged(nameof(LastChecked));
            return ok;
        }

        private static bool IsNew(UpdateAnswer a) => a.Kind is (UpdatePromptKind.Notice or UpdatePromptKind.Forced) && CleanVersion(a.Version) != null;

        /// <summary>Only a notice and the screen have a version, notes and a message: of anything else nothing is kept.</summary>
        private static UpdateAnswer Told(UpdateAnswer a)
        {
            if (!a.Enabled || a.Kind == UpdatePromptKind.None || a.Kind == UpdatePromptKind.Notice && !IsNew(a)) return UpdateAnswer.Off;
            return a.Kind == UpdatePromptKind.RequiredBar ? new UpdateAnswer { Enabled = true, Kind = a.Kind } : a;
        }

        private void Show()
        {
            var stage = _answer.Kind switch
            {
                UpdatePromptKind.Notice when IsNew(_answer) && !IsDismissed(_answer.Version) => UpdateStage.Available,
                UpdatePromptKind.RequiredBar => UpdateStage.Required,
                // Once it is there it stays: what is started behind it goes on
                UpdatePromptKind.Forced => View.Stage != UpdateStage.Forced && _isBusy() ? UpdateStage.Waiting : UpdateStage.Forced,
                _ => UpdateStage.Hidden,
            };
            // Downloaded in the background: told of when it is ready
            if (stage == UpdateStage.Available && Installs && InstallsAutomatically && !_isAsked && _step != UpdateStep.Ready) stage = UpdateStage.Hidden;
            View = stage == UpdateStage.Hidden ? UpdateView.None : ViewOf(stage);
            if (IsWorkAwaited && !_isWaitingForWork) _ = WaitForWorkAsync();
        }

        private bool IsStaging => _staging is { IsCompleted: false };

        private bool Installs => _self != null && _answer.CanInstall && _answer.Tag != null && IsNew(_answer);

        private UpdateView ViewOf(UpdateStage stage) => new UpdateView(stage, _answer, Installs, Installs ? _step : UpdateStep.None, _progress, IsReady && _isBusy(), IsReady && _self!.IsOpenElsewhere);

        private bool IsReady => Installs && _step == UpdateStep.Ready;

        private bool MayNotRestart => _isBusy() || _self!.IsOpenElsewhere;

        /// <summary>The screen waits for work, or Restart now does.</summary>
        private bool IsWorkAwaited => View.Stage == UpdateStage.Waiting || View.Stage != UpdateStage.Hidden && View.Step == UpdateStep.Ready;

        /// <summary>
        /// After each answer: what is staged is still offered and still trusted (a newer key list may have come with it), and
        /// below the minimum there is an update this copy can really get, or the screen is a bar (wiki §3.7).
        /// </summary>
        private async Task LookAgainAsync()
        {
            // The end has its own last look
            if (_self == null || _isEnding) return;
            if (_answer.Tag != _tag && !IsStaging)
            {
                _tag = _answer.Tag;
                _isAsked = false;
                if (_ready == null) _step = UpdateStep.None;
            }

            if (_ready != null && (!Installs || _answer.Tag != _ready.Tag)) Drop();
            if (_ready is { } ready)
            {
                // The answer has come anew: its words are still the manifest's
                if (await IsStillGoodAsync()) Take(ready);
            }
            else if (_answer.Kind == UpdatePromptKind.Forced && Installs)
            {
                try
                {
                    Take(await _self.VerifyAsync(_answer.Tag!, _answer.Version!, _stop));
                }
                catch (UpdateRefusedException ex)
                {
                    await RefusedAsync(_answer.Tag!, ex.Reason);
                }
                catch (UpdateCheckFailedException ex)
                {
                    Logger.Warn("the required update could not be read", ex);
                    NothingToUpdateTo();
                }
            }
        }

        /// <summary>The version and the notes are the signed manifest's.</summary>
        private void Take(VerifiedUpdate update)
        {
            _answer.Version = update.Version;
            _answer.NotesEn = update.NotesEn;
            _answer.NotesAr = update.NotesAr;
        }

        private void BeginQuietly()
        {
            if (Installs && InstallsAutomatically && _answer.Kind == UpdatePromptKind.Notice && _ready == null && !IsStaging) _staging = StageAsync();
        }

        private async Task StageAsync()
        {
            while (true)
            {
                var tag = _answer.Tag!;
                await StageAsync(tag, _answer.Version!);
                // Another release was offered meanwhile: that one now, not at the next check
                if (_answer.Tag == tag || !Installs || _stop.IsCancellationRequested) return;
                if (!_isAsked && !(InstallsAutomatically && _answer.Kind == UpdatePromptKind.Notice)) return;
                _tag = _answer.Tag;
            }
        }

        private async Task StageAsync(string tag, string version)
        {
            _step = UpdateStep.Downloading;
            _progress = 0;
            Show();
            try
            {
                var update = await _self!.StageAsync(tag, version, new Percents(p =>
                {
                    _progress = p;
                    if (View.Step == UpdateStep.Downloading) Show();
                }), _stop);
                // Another answer came meanwhile: this is not what it offers
                if (!Installs || _answer.Tag != tag || !_isAsked && !InstallsAutomatically)
                {
                    Drop();
                }
                else
                {
                    Take(update);
                    _ready = update;
                    _step = UpdateStep.Ready;
                }
            }
            catch (UpdateRefusedException ex)
            {
                await RefusedAsync(tag, ex.Reason);
            }
            catch (UpdateCheckFailedException ex)
            {
                // The network or the disk: no refusal. Asked for, it says so; else the next check tries again
                Logger.Warn("the update could not be downloaded", ex);
                _step = _isAsked ? UpdateStep.Failed : UpdateStep.None;
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // However it goes wrong, nothing is installed, and the app and its schedule go on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Logger.Error("the update could not be prepared", ex);
                Drop();
                _step = _isAsked ? UpdateStep.Failed : UpdateStep.None;
            }

            Show();
        }

        /// <summary>Whether what is staged may still be installed; if not it is dropped, and the server is told.</summary>
        private async Task<bool> IsStillGoodAsync()
        {
            if (_ready is not { } ready) return false;
            try
            {
                _self!.VerifyStaged();
                return true;
            }
            catch (UpdateRefusedException ex)
            {
                await RefusedAsync(ready.Tag, ex.Reason);
                return false;
            }
        }

        /// <summary>Nothing is installed and no link is offered (wiki §5.3 step 6); below the minimum the app stays usable behind a bar.</summary>
        private async Task RefusedAsync(string tag, string reason)
        {
            Drop();
            // Another release may be offered by now: that one was not refused
            if (_answer.Tag == tag) NothingToUpdateTo();
            Show();
            await _self!.ReportAsync(tag, reason, _stop);
        }

        private void NothingToUpdateTo()
        {
            _answer = _answer.Kind == UpdatePromptKind.Forced ? new UpdateAnswer { Enabled = true, Kind = UpdatePromptKind.RequiredBar } : UpdateAnswer.Off;
            Result = _answer.Kind == UpdatePromptKind.RequiredBar ? UpdateResult.Required : UpdateResult.None;
        }

        private void Drop()
        {
            // The end looks at it once more itself; what it looks at is not taken away under it
            if (_isEnding) return;
            _ready = null;
            _step = UpdateStep.None;
            _self?.Discard();
        }

        /// <summary>
        /// The button when it is ready: ends the app, unless work is running. The app's end is the only one that installs
        /// (<see cref="BeginEnd"/>, <see cref="HandOver"/>); this one also starts the app again.
        /// </summary>
        private async Task RestartAsync()
        {
            if (_isEnding) return;
            if (MayNotRestart)
            {
                Show();
                return;
            }

            // Pressed: what goes wrong from here on is told
            _isAsked = true;
            // Looked at again after it: work, or another copy, may have begun meanwhile
            if (await IsStillGoodAsync() && !MayNotRestart)
            {
                IsRestartAsked = true;
                _close?.Invoke();
            }
            else
            {
                Show();
            }
        }

        /// <summary>Restart now was pressed: an end without an updater starts the app again.</summary>
        public bool IsRestartAsked { get; private set; }

        /// <summary>The app is to start again after its end, for another reason than an update.</summary>
        public void AskRestart() => IsRestartAsked = true;

        /// <summary>
        /// The app's end begins, before any work stops (stopped work no longer says that it was running): an update is installed
        /// only if it is ready and no work runs. Nothing else is done with updates after it.
        /// </summary>
        public void BeginEnd()
        {
            if (_isEnding) return;
            _mayInstall = _self != null && _ready != null && !MayNotRestart;
            _isEnding = true;
        }

        /// <summary>
        /// The work has stopped: what is ready is checked once more and handed to the updater, which waits for the app to end.
        /// On any thread. Whether it was.
        /// </summary>
        public bool HandOver()
        {
            if (!_mayInstall || _isHandedOver || _ready is not { } ready) return false;
            try
            {
                _self!.VerifyStaged();
            }
            catch (UpdateRefusedException ex)
            {
                Logger.Warn("the update was refused as the app ended: " + ex.Reason);
                // The server is told, but the end does not wait long for it
                _self!.ReportAsync(ready.Tag, ex.Reason).Wait(ReportLimit);
                return false;
            }

            // A second copy may have been started while the work stopped: the updater would end that one
            if (_self.IsOpenElsewhere) return false;
            _self.ApplyOnExit(IsRestartAsked);
            _isHandedOver = true;
            return true;
        }

        private async Task WaitForWorkAsync()
        {
            _isWaitingForWork = true;
            try
            {
                while (IsWorkAwaited)
                {
                    await _delay(BusyPoll, _stop);
                    if (View.Stage == UpdateStage.Waiting ? !_isBusy() : IsWorkAwaited && View.IsBusy != MayNotRestart) Show();
                }
            }
            catch (OperationCanceledException)
            {
                // The app closes
            }
            finally
            {
                _isWaitingForWork = false;
            }
        }

        /// <summary>The bar's ×: not again for this version.</summary>
        public void Dismiss()
        {
            if (!View.CanClose || View.Version is not { } version) return;
            lock (_memory)
            {
                var dismissed = _memory.Dismissed ??= new List<string>();
                dismissed.Remove(version);
                dismissed.Add(version);
                if (dismissed.Count > MaxDismissed) dismissed.RemoveRange(0, dismissed.Count - MaxDismissed);
            }

            Keep();
            View = UpdateView.None;
        }

        /// <summary>The button of the bar, of the screen and of Settings.</summary>
        public void Begin()
        {
            if (!Installs)
            {
                _delivery.Begin();
            }
            else if (_ready != null)
            {
                _ = RestartAsync();
            }
            else
            {
                // Downloading in the background already: now it is shown
                _isAsked = true;
                if (IsStaging) Show();
                else _staging = StageAsync();
            }
        }

        /// <summary>The download's progress in whole percents, on the thread that asked.</summary>
        private sealed class Percents : IProgress<double>
        {
            private readonly SynchronizationContext? _context = SynchronizationContext.Current;
            private readonly Action<double> _told;
            private int _last = -1;

            public Percents(Action<double> told) => _told = told;

            public void Report(double value)
            {
                var percent = (int)(Math.Min(1, Math.Max(0, value)) * 100);
                if (percent == _last) return;
                _last = percent;
                if (_context == null) _told(percent / 100.0);
                else _context.Post(_ => _told(percent / 100.0), null);
            }
        }

        private bool IsDismissed(string? version) => version != null && _memory.Dismissed?.Contains(version) == true;

        private void Keep()
        {
            // Also from a download's own thread
            lock (_memory)
            {
                _memory.Core = _checker.State();
                if (!_store.Save(_memory)) Logger.Warn("the update state could not be saved");
            }
        }

        /// <summary>A version as ours are, else null: nothing else of the network's is shown as one.</summary>
        internal static string? CleanVersion(string? version) => version != null && VersionShape.IsMatch(version) ? version : null;

        /// <summary>Plain text of the network's: its lines, without controls or marks that turn the text around it.</summary>
        internal static string CleanText(string? text)
        {
            var clean = new StringBuilder();
            foreach (var c in (text ?? "").Replace("\r\n", "\n"))
            {
                var turns = c >= '‪' && c <= '‮' || c >= '⁦' && c <= '⁩';
                if (c == '\n' || c == '\t' || !char.IsControl(c) && !turns) clean.Append(c);
            }

            if (clean.Length > MaxText)
            {
                clean.Length = char.IsHighSurrogate(clean[MaxText - 1]) ? MaxText - 1 : MaxText;
                clean.Append('…');
            }

            return clean.ToString().Trim();
        }
    }

    /// <summary>What the button does in a copy that does not install in place.</summary>
    public interface IUpdateDelivery
    {
        void Begin();
    }

    public sealed class DownloadPageDelivery : IUpdateDelivery
    {
        private readonly IBrowserLauncher _browser;

        public DownloadPageDelivery(IBrowserLauncher browser) => _browser = browser;

        public void Begin()
        {
            try
            {
                _browser.Open(UpdateInstalls.DownloadPage);
            }
#pragma warning disable CA1031 // No browser to open: the page's address is on the screen.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.For("updates").Warn("the download page did not open", ex);
            }
        }
    }
}
