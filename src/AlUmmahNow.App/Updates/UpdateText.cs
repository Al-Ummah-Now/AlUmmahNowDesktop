using System;
using System.IO;
using System.Reflection;
using AlUmmahNow.App.I18n;

namespace AlUmmahNow.App.Updates
{
    /// <summary>Every word of the bar, the screen and Settings → Updates, in the active language.</summary>
    public sealed class UpdateText
    {
        private readonly Localizer _l;
        private readonly UpdateApp _app;
        private readonly UpdateService _service;

        public UpdateText(Localizer l, UpdateApp app, UpdateService service)
        {
            _l = l;
            _app = app;
            _service = service;
        }

        public bool IsRtl => _l.IsRtl;

        public string this[string key] => _l[key];

        public string Bar(UpdateView v) => v.Stage switch
        {
            UpdateStage.Available or UpdateStage.Waiting when v.Step != UpdateStep.None => Step(v),
            UpdateStage.Available => _l.Format("updates.available", _l.Version(v.Version ?? "")),
            UpdateStage.Waiting => _l[_app == UpdateApp.Studio ? "updates.waiting.studio" : "updates.waiting.viewer"],
            UpdateStage.Required => _l["updates.required"],
            _ => "",
        };

        /// <summary>How far an update is that the app installs itself; empty before it is asked for.</summary>
        public string Step(UpdateView v) => v.Step switch
        {
            UpdateStep.Downloading => _l.Format("updates.downloading", _l.Percent(v.Progress)),
            UpdateStep.Ready => _l[v.IsBusy ? _app == UpdateApp.Studio ? "updates.readyBusy.studio" : "updates.readyBusy.viewer" : v.Stage == UpdateStage.Forced ? "updates.readyForced" : "updates.ready"],
            UpdateStep.Failed => _l["updates.notDownloaded"],
            _ => "",
        };

        /// <summary>The button: installs, restarts, or opens the download page.</summary>
        public string Button(UpdateView v) => _l[!v.Installs ? "updates.download" : v.Step == UpdateStep.Ready ? "updates.restart" : "updates.update"];

        /// <summary>The notes in the app's language; empty when there are none.</summary>
        public string Notes(UpdateView v) => _l.IsRtl ? v.NotesAr : v.NotesEn;

        public string NotesTitle(UpdateView v) => _l.Format("updates.notesTitle", _l.Version(v.Version ?? ""));

        public string Message(UpdateView v) => _l.IsRtl ? v.MessageAr : v.MessageEn;

        public string ForcedBody(UpdateView v) => v.MinimumVersion is { } minimum
            ? _l.Format("updates.forced.body", _l.Version(_service.InstalledVersion), _l.Version(minimum))
            : _l.Format("updates.forced.bodyNew", _l.Version(_service.InstalledVersion), _l[_app == UpdateApp.Studio ? "studio.name" : "app.name"]);

        /// <summary>How a person updates this copy.</summary>
        public string How => _l[UpdateInstalls.HowKey(_service.Install)];

        /// <summary>What they type for it; empty when they only open the file.</summary>
        public string Command => UpdateInstalls.Command(_service.Install, _app) ?? "";

        /// <summary>The page the button opens, to read and to copy. Left to right in both languages.</summary>
        public string Page => _l.Format("updates.page", "⁦" + UpdateInstalls.DownloadPage.Host + UpdateInstalls.DownloadPage.AbsolutePath + "⁩");

        public string Version => _l.Format("updates.version", _l.Version(_service.InstalledVersion));

        public string LastChecked => _service.LastChecked is { } at ? _l.Format("updates.lastChecked", _l.DateTime(at)) : _l["updates.neverChecked"];

        /// <summary>What the last check came to; empty before one.</summary>
        public string Result => _service.Result switch
        {
            UpdateResult.Checking => _l["updates.checking"],
            UpdateResult.UpToDate => _l["updates.upToDate"],
            UpdateResult.Found when _service.Found.Version is { } version => _l.Format("updates.found", _l.Version(version)),
            UpdateResult.Required => _l["updates.required"],
            UpdateResult.Failed when _service.IsCheckAsked => _l["updates.failed"],
            _ => "",
        };

        /// <summary>The app's icon (PNG).</summary>
        public static Stream Icon(UpdateApp app)
        {
            var name = "icons/net.alummahnow." + (app == UpdateApp.Studio ? "Studio" : "AlUmmahNow") + ".png";
            return typeof(UpdateText).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        }

        /// <summary>The version of an app's assembly, as the release workflow stamped it ("1.5.0-preview.3").</summary>
        public static string VersionOf(Assembly assembly)
        {
            var stamped = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return UpdateService.CleanVersion(stamped.Split('+')[0]) ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
