using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Updates;
using AlUmmahNow.CoreBridge;

namespace AlUmmahNow.App.Demo
{
    /// <summary>The answers of the update scenes (<c>AUN_DEMO_UPDATE</c>). No network, never in a normal launch.</summary>
    public sealed class DemoUpdateChecker : IUpdateChecker
    {
        public const string Installed = "1.4.2", Newest = "1.5.0", Minimum = "1.4.8";
        private readonly string _scene;

        public DemoUpdateChecker(string scene) => _scene = scene;

        public Task<UpdateAnswer> CheckAsync(CancellationToken cancellationToken = default)
        {
            if (_scene == "offline") throw new UpdateCheckFailedException("offline (demo)");
            var kind = _scene switch
            {
                "off" or "uptodate" => UpdatePromptKind.None,
                "forced" or "waiting" => UpdatePromptKind.Forced,
                "required" => UpdatePromptKind.RequiredBar,
                _ => UpdatePromptKind.Notice,
            };
            return Task.FromResult(new UpdateAnswer
            {
                Enabled = _scene != "off",
                Kind = kind,
                Version = Newest,
                MinimumVersion = kind is UpdatePromptKind.Forced or UpdatePromptKind.RequiredBar ? Minimum : "0.0.0",
                NotesEn = "Uploads continue where they stopped after a restart.\nThe schedule shows times in your time zone.",
                NotesAr = "تستأنف عمليات الرفع من حيث توقفت بعد إعادة التشغيل.\nيعرض الجدول الأوقات حسب منطقتك الزمنية.",
                MessageEn = kind == UpdatePromptKind.Forced ? "This version can no longer sign in." : null,
                MessageAr = kind == UpdatePromptKind.Forced ? "لم يعد بإمكان هذا الإصدار تسجيل الدخول." : null,
            });
        }

        public UpdatePromptKind LastKnown() => UpdatePromptKind.None;

        public string State() => "";
    }

    /// <summary>Counts what would have opened the browser.</summary>
    public sealed class DemoDelivery : IUpdateDelivery
    {
        public int Begun { get; private set; }

        public void Begin() => Begun++;
    }

    /// <summary>What the views say is on the screen, and what a scene presses in them.</summary>
    public interface IUpdateViews
    {
        bool IsBarShown { get; }
        string BarText { get; }
        bool IsCloseShown { get; }
        bool IsDownloadShown { get; }
        bool IsNotesShown { get; }

        /// <summary>The notes' dialog is open, with this text.</summary>
        string OpenNotes { get; }

        bool IsForcedShown { get; }
        string ForcedText { get; }

        /// <summary>The screen fills the window under its header, and what was there cannot be reached.</summary>
        bool IsForcedWhole { get; }

        /// <summary>The keyboard is on the screen's button.</summary>
        bool IsForcedFocused { get; }

        bool IsSectionShown { get; }
        string SectionText { get; }
        void PressClose();
        void PressNotes();

        /// <summary>The bar's or the screen's button, whichever is there.</summary>
        void PressDownload();

        void PressCheckNow();
    }

    /// <summary>
    /// The scenes of CI: each runs the app's own <see cref="UpdateService"/> on a demo answer, presses the app's own
    /// controls, and passes only when the views say that the screen is as it must be.
    /// </summary>
    public sealed class UpdateScene
    {
        public const string Variable = "AUN_DEMO_UPDATE";

        /// <summary>Another scene's answer under this one: CI sees that a scene that is not as it must be fails.</summary>
        public const string AnswerVariable = "AUN_DEMO_UPDATE_ANSWER";

        public static readonly IReadOnlyList<string> All = new[] { "bar", "notes", "dismissed", "forced", "waiting", "required", "settings", "uptodate", "offline", "off" };

        private readonly Localizer _l;
        private readonly DemoDelivery _delivery = new DemoDelivery();
        private TimeSpan _passed = TimeSpan.Zero;
        private bool _wasBarShown;

        private UpdateScene(string name, string answer, Localizer l, UpdateInstall install, Func<bool>? isBusy)
        {
            Name = name;
            _l = l;
            var store = new UpdateStore(null);
            Service = new UpdateService(new DemoUpdateChecker(answer), store, install, DemoUpdateChecker.Installed, isBusy ?? (() => answer == "waiting"), _delivery,
                () => DemoClock.Now + _passed,
                async (wait, stop) =>
                {
                    // The work that "waiting" waits for is the app's own, and has begun by then
                    await Task.Delay(TimeSpan.FromSeconds(name == "waiting" && wait > UpdateService.BusyPoll ? 10 : 1), stop);
                    _passed += wait;
                },
                wasEnabled: name == "offline");
        }

        public string Name { get; }
        public UpdateService Service { get; }

        /// <summary>Settings → Updates is open in these.</summary>
        public bool IsInSettings => Name is "settings" or "uptodate" or "offline" or "off";

        /// <summary>The scene of the environment, or null in a normal launch.</summary>
        /// <param name="isBusy">What the app itself would say.</param>
        public static UpdateScene? FromEnvironment(Localizer l, UpdateInstall install, Func<bool> isBusy)
        {
            var name = Environment.GetEnvironmentVariable(Variable) ?? "";
            return name.Length == 0 ? null : Named(name, l, install, Environment.GetEnvironmentVariable(AnswerVariable), isBusy);
        }

        public static UpdateScene Named(string name, Localizer l, UpdateInstall install, string? answer = null, Func<bool>? isBusy = null) =>
            All.Contains(name) ? new UpdateScene(name, string.IsNullOrEmpty(answer) ? name : answer!, l, install, isBusy) : throw new ArgumentException("no update scene " + name);

        /// <summary>What a person does, once the first answer is there.</summary>
        public async Task ActAsync(IUpdateViews views)
        {
            switch (Name)
            {
                case "bar":
                case "forced":
                    views.PressDownload();
                    break;
                case "notes":
                    views.PressNotes();
                    break;
                case "dismissed":
                    _wasBarShown = views.IsBarShown;
                    views.PressClose();
                    // Another check does not bring it back
                    await Service.CheckAsync();
                    break;
                case "uptodate":
                case "offline":
                    views.PressCheckNow();
                    break;
            }
        }

        /// <summary>Null when the screen is as it must be, else what is wrong.</summary>
        public string? Wrong(IUpdateViews v)
        {
            var newest = _l.Version(DemoUpdateChecker.Newest);
            var installed = _l.Version(DemoUpdateChecker.Installed);
            string? Check(params (bool Ok, string What)[] all) => all.Where(x => !x.Ok).Select(x => x.What).FirstOrDefault();
            return Name switch
            {
                "bar" => Check((v.IsBarShown, "no bar"), (v.BarText.Contains(newest), "the bar has no version"), (v.IsCloseShown, "no close"), (v.IsDownloadShown, "no download"),
                    (v.IsNotesShown, "no what's new"), (!v.IsForcedShown, "a screen"), (_delivery.Begun == 1, "download did nothing")),
                "notes" => Check((v.IsBarShown, "no bar"), (v.IsCloseShown, "no close"), (v.OpenNotes.Contains(_l.IsRtl ? "الرفع" : "Uploads"), "no notes")),
                "dismissed" => Check((_wasBarShown, "there was no bar"), (!v.IsBarShown, "the bar is still there"), (!v.IsForcedShown, "a screen"), (Service.Result == UpdateResult.Found, "nothing was found")),
                "forced" => Check((v.IsForcedShown, "no screen"), (v.IsForcedWhole, "the screen is not the whole window"), (v.IsForcedFocused, "the keyboard is elsewhere"), (!v.IsBarShown, "a bar"),
                    (v.ForcedText.Contains(installed) && v.ForcedText.Contains(_l.Version(DemoUpdateChecker.Minimum)), "the screen has no versions"),
                    (v.ForcedText.Contains(_l.IsRtl ? "تسجيل الدخول" : "sign in"), "no message"), (_delivery.Begun == 1, "download did nothing")),
                "waiting" => Check((v.IsBarShown, "no bar"), (!v.IsCloseShown, "it can be closed"), (!v.IsForcedShown, "a screen"), (v.IsDownloadShown, "no download")),
                "required" => Check((v.IsBarShown, "no bar"), (!v.IsCloseShown, "it can be closed"), (!v.IsDownloadShown, "nothing to download"), (!v.IsNotesShown, "what's new in nothing"), (!v.IsForcedShown, "a screen")),
                "settings" => Check((v.IsSectionShown, "no section"), (v.SectionText.Contains(installed), "no version"), (v.SectionText.Contains(newest), "nothing found"), (v.IsBarShown, "no bar"), (v.IsCloseShown, "no close")),
                "uptodate" => Check((v.IsSectionShown, "no section"), (v.SectionText.Contains(_l["updates.upToDate"]), "not up to date"), (!v.IsBarShown, "a bar")),
                "offline" => Check((v.IsSectionShown, "no section"), (v.SectionText.Contains(_l["updates.failed"]), "it is not told"), (v.SectionText.Contains(_l["updates.neverChecked"]), "it was checked"), (!v.IsBarShown, "a bar")),
                "off" => Check((!v.IsSectionShown, "a section"), (!v.IsBarShown, "a bar"), (!v.IsForcedShown, "a screen"), (Service.Result == UpdateResult.UpToDate, "it was not checked")),
                _ => "no such scene",
            };
        }
    }
}
