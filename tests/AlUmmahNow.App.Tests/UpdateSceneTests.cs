using AlUmmahNow.App.Demo;
using AlUmmahNow.App.I18n;
using AlUmmahNow.App.Updates;
using Xunit;

namespace AlUmmahNow.App.Tests;

/// <summary>CI's update scenes pass on a window that is right, and on no other.</summary>
public sealed class UpdateSceneTests
{
    /// <summary>A window that shows what the service says, with one fault when asked for.</summary>
    private sealed class Window : IUpdateViews
    {
        private readonly UpdateScene _scene;
        private readonly UpdateText _text;
        private bool _notes;

        public Window(UpdateScene scene, Localizer l)
        {
            _scene = scene;
            _text = new UpdateText(l, UpdateApp.Studio, scene.Service);
        }

        public string Fault = "";
        private UpdateService S => _scene.Service;
        private UpdateView V => S.View;

        public bool IsBarShown => Fault switch { "no-bar" => false, "bar" => true, _ => V.IsBar };
        public string BarText => Fault == "western" ? "1.5.0" : Fault == "mute" ? "" : _text.Bar(V);
        public bool IsCloseShown => Fault switch { "close" => true, "no-close" => false, _ => V.CanClose };
        public bool IsDownloadShown => Fault switch { "download" => true, "no-download" => false, _ => V.CanDownload };
        public bool IsNotesShown => Fault == "notes" || Fault != "no-notes" && V.CanDownload && _text.Notes(V).Length > 0;
        public string OpenNotes => _notes && Fault != "no-dialog" ? _text.Notes(S.Found) : "";
        public bool IsForcedShown => Fault switch { "forced" => true, "no-forced" => false, _ => V.Stage == UpdateStage.Forced };
        public string ForcedText => Fault == "mute" ? "" : _text.ForcedBody(V) + _text.Message(V) + _text.Step(V);
        public bool IsForcedWhole => IsForcedShown && Fault != "part";
        public bool IsForcedFocused => Fault != "elsewhere";
        public bool IsSectionShown => Fault switch { "section" => true, "no-section" => false, _ => S.IsSectionVisible && _scene.IsInSettings };
        public string SectionText => Fault == "mute" ? "" : _text.Version + _text.LastChecked + _text.Result + _text.Step(S.Found);

        public string ButtonText => Fault == "other-button" ? _text["updates.download"] : _text.Button(V);
        public bool IsButtonEnabled => Fault switch { "enabled" => true, "disabled" => false, _ => V.CanBegin };
        public bool IsAutoShown => Fault != "no-auto" && S.CanInstall;
        public bool IsAutoOn => Fault != "auto-off" && S.InstallsAutomatically;

        public void PressAuto()
        {
            if (Fault != "dead") S.InstallsAutomatically = !S.InstallsAutomatically;
        }

        public void PressClose()
        {
            if (Fault != "dead") S.Dismiss();
        }

        public void PressNotes() => _notes = Fault != "dead";

        public void PressDownload()
        {
            if (Fault != "dead") S.Begin();
        }

        public void PressCheckNow()
        {
            if (Fault != "dead") _ = S.CheckAsync(isAsked: true);
        }
    }

    private static async Task<string?> RunAsync(string name, string language, string fault = "", string? answer = null)
    {
        var l = new Localizer(language, TimeZoneInfo.Utc);
        var scene = UpdateScene.Named(name, l, UpdateInstall.Deb, answer);
        var window = new Window(scene, l) { Fault = fault };
        await scene.Service.CheckAsync();
        await scene.ActAsync(window);
        return scene.Wrong(window);
    }

    public static IEnumerable<object[]> Scenes() => from scene in UpdateScene.All.Concat(UpdateScene.Installing) from language in new[] { "en", "ar" } select new object[] { scene, language };

    [Theory]
    [MemberData(nameof(Scenes))]
    public async Task A_window_that_is_right_passes(string scene, string language) => Assert.Null(await RunAsync(scene, language));

    [Theory]
    [InlineData("bar", "no-bar forced no-close no-download no-notes mute dead western")]
    [InlineData("notes", "no-bar no-dialog dead")]
    [InlineData("dismissed", "bar no-bar forced dead")]
    [InlineData("forced", "no-forced part elsewhere bar mute dead")]
    [InlineData("waiting", "no-bar close forced no-download")]
    [InlineData("required", "no-bar close download notes forced")]
    [InlineData("settings", "no-section mute no-bar")]
    [InlineData("uptodate", "no-section mute bar")]
    [InlineData("offline", "no-section mute bar dead")]
    [InlineData("off", "section bar forced")]
    [InlineData("update", "no-bar mute other-button disabled no-close no-notes")]
    [InlineData("downloading", "no-bar mute other-button enabled close dead")]
    [InlineData("failed", "no-bar mute other-button disabled dead")]
    [InlineData("ready", "no-bar mute other-button no-close dead")]
    [InlineData("readybusy", "no-bar mute other-button enabled")]
    [InlineData("forcedupdate", "no-forced part elsewhere other-button disabled bar")]
    [InlineData("forceddownloading", "no-forced part mute other-button enabled bar dead")]
    [InlineData("forcedready", "no-forced elsewhere mute other-button dead")]
    [InlineData("settingsupdate", "no-section mute no-auto auto-off other-button dead")]
    public async Task A_window_with_a_fault_does_not(string scene, string faults)
    {
        foreach (var fault in faults.Split(' ')) Assert.True(await RunAsync(scene, "ar", fault) != null, scene + " passes with " + fault);
    }

    /// <summary>The same with another answer of the server: the scene looks at the window, not at itself.</summary>
    [Theory]
    [MemberData(nameof(Others))]
    public async Task Another_answer_does_not(string scene, string answer) => Assert.NotNull(await RunAsync(scene, "en", answer: answer));

    public static IEnumerable<object[]> Others()
    {
        // These ask the same of the server, and differ in what a person does
        var same = new[] { "bar", "notes", "dismissed", "settings" };
        return from scene in UpdateScene.All.Concat(UpdateScene.Installing) from answer in UpdateScene.All
               where scene != answer && !(same.Contains(scene) && same.Contains(answer))
               select new object[] { scene, answer };
    }

    [Fact]
    public void A_scene_that_is_none_stops_the_run() => Assert.Throws<ArgumentException>(() => UpdateScene.Named("bars", new Localizer("en"), UpdateInstall.Deb));
}
