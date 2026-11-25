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
    public enum UpdateResult { None, Checking, UpToDate, Found, Failed }

    /// <summary>Everything the views show of an update. Replaced, never changed.</summary>
    public sealed class UpdateView
    {
        public static readonly UpdateView None = new UpdateView(UpdateStage.Hidden, UpdateAnswer.Off);

        internal UpdateView(UpdateStage stage, UpdateAnswer answer)
        {
            Stage = stage;
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

        public bool IsBar => Stage == UpdateStage.Available || Stage == UpdateStage.Required || Stage == UpdateStage.Waiting;
        public bool CanClose => Stage == UpdateStage.Available;

        /// <summary>There is a version to download.</summary>
        public bool CanDownload => Stage == UpdateStage.Available || Stage == UpdateStage.Waiting || Stage == UpdateStage.Forced;
    }

    /// <summary>
    /// The app's check for a new version (wiki AUN-APP-UPDATES): about 30 s after launch, every <c>checkHours</c>, and on
    /// Check now. It only tells: the app downloads and installs nothing, and its one link is compiled in
    /// (<see cref="UpdateInstalls.DownloadPage"/>, opened by <see cref="IUpdateDelivery"/>, where installing in place goes).
    /// Everything runs on the thread that made it, the UI thread.
    /// </summary>
    public sealed class UpdateService : ObservableObject
    {
        public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);

        /// <summary>The longest single wait: a computer that slept is asked for soon after it wakes.</summary>
        public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(10);

        /// <summary>How often running work is looked at while the screen waits for it.</summary>
        public static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(2);

        public const int MaxText = 1_500;
        public const int MaxDismissed = 10;

        private static readonly Logger Logger = Log.For("updates");
        private static readonly Regex VersionShape = new Regex(@"\A[0-9]{1,6}(\.[0-9]{1,6}){2,3}(-[0-9A-Za-z.]{1,24})?\z", RegexOptions.CultureInvariant);

        private readonly IUpdateChecker _checker;
        private readonly UpdateStore _store;
        private readonly UpdateMemory _memory;
        private readonly Func<bool> _isBusy;
        private readonly IUpdateDelivery _delivery;
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

        /// <param name="isBusy">An upload, an encode or a live session is running: the screen waits for it.</param>
        /// <param name="wasEnabled">Review frames: the switch was on in an earlier run.</param>
        public UpdateService(IUpdateChecker checker, UpdateStore store, UpdateInstall install, string installedVersion, Func<bool> isBusy, IUpdateDelivery delivery,
            Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null, bool wasEnabled = false)
        {
            _checker = checker;
            _store = store;
            _memory = store.Load();
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
        public UpdateView Found => new UpdateView(_view.Stage, _answer);

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
                Keep();
                _answer = answer.Enabled ? answer : UpdateAnswer.Off;
                Result = IsNew(_answer) ? UpdateResult.Found : UpdateResult.UpToDate;
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

            if (ok) Show();
            OnPropertyChanged(nameof(IsSectionVisible));
            OnPropertyChanged(nameof(LastChecked));
            return ok;
        }

        private static bool IsNew(UpdateAnswer a) => a.Kind != UpdatePromptKind.None && CleanVersion(a.Version) != null;

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
            View = stage == UpdateStage.Hidden ? UpdateView.None : new UpdateView(stage, _answer);
            if (stage == UpdateStage.Waiting && !_isWaitingForWork) _ = WaitForWorkAsync();
        }

        private async Task WaitForWorkAsync()
        {
            _isWaitingForWork = true;
            try
            {
                while (View.Stage == UpdateStage.Waiting)
                {
                    await _delay(BusyPoll, _stop);
                    if (View.Stage == UpdateStage.Waiting && !_isBusy()) Show();
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
            var dismissed = _memory.Dismissed ??= new List<string>();
            dismissed.Remove(version);
            dismissed.Add(version);
            if (dismissed.Count > MaxDismissed) dismissed.RemoveRange(0, dismissed.Count - MaxDismissed);
            Keep();
            View = UpdateView.None;
        }

        /// <summary>The button of the bar, of the screen and of Settings.</summary>
        public void Begin() => _delivery.Begin();

        private bool IsDismissed(string? version) => version != null && _memory.Dismissed?.Contains(version) == true;

        private void Keep()
        {
            _memory.Core = _checker.State();
            if (!_store.Save(_memory)) Logger.Warn("the update state could not be saved");
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

    /// <summary>
    /// What the button does. Today there is one: the download page in the browser. An app that installs in place is another,
    /// in a later change (wiki AUN-APP-UPDATES §5).
    /// </summary>
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
