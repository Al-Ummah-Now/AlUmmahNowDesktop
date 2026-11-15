using AlUmmahNow.App.I18n;
using AlUmmahNow.CoreBridge.Studio;
using AlUmmahNow.Studio.Captions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AlUmmahNow.Studio.WindowsApp;

/// <summary>Captions of a video: drafts with their origin, server or on-device transcription, and the cue editor.</summary>
internal sealed class CaptionsPage
{
    private readonly Localizer _l;
    private readonly CaptionsViewModel _vm;
    private readonly Func<Task<string?>> _pickMedia;
    private readonly Grid _host = new();
    private readonly StackPanel _list = new() { Spacing = 12, Padding = new Thickness(20) };
    private readonly ComboBox _video;
    private readonly TextBlock _status = Ui.Text("", dim: true);
    private readonly InfoBar _error = Ui.Bar(InfoBarSeverity.Error);
    private readonly InfoBar _notice = Ui.Bar(InfoBarSeverity.Success);
    private readonly StackPanel _drafts = new() { Spacing = 8 };
    private readonly StackPanel _jobs = new() { Spacing = 6 };
    private readonly TextBlock _minutes = Ui.Text("", dim: true);
    private readonly ComboBox _language;
    private readonly Button _server;
    private readonly StackPanel _device = Ui.Card();
    private readonly ComboBox _model;
    private readonly Button _transcribe;
    private readonly Button _cancelDevice;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1 };
    private readonly TextBlock _progressText = Ui.Text("", dim: true);
    private IReadOnlyList<string> _videoIds = Array.Empty<string>();
    private IReadOnlyList<string> _modelIds = Array.Empty<string>();
    private bool _syncing;

    public CaptionsPage(Localizer l, CaptionsViewModel vm, Func<XamlRoot?> root, Func<Task<string?>> pickMedia)
    {
        _l = l;
        _vm = vm;
        _pickMedia = pickMedia;
        _vm.Confirm = ConfirmKeys.For(root, l);
        Root = _host;
        _host.Children.Add(_list);

        _video = new ComboBox { Header = l["studio.captions.video"].Replace("_", "", StringComparison.Ordinal), MinWidth = 320 };
        AutomationProperties.SetAutomationId(_video, "CaptionsVideo");
        _video.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var i = _video.SelectedIndex;
            if (i >= 0 && i < _videoIds.Count) _vm.Video = _vm.Videos.First(v => v.Id == _videoIds[i]);
        };
        _list.Children.Add(_video);
        _list.Children.Add(_status);
        _list.Children.Add(_error);
        _list.Children.Add(_notice);

        _list.Children.Add(Ui.Heading(l["studio.captions.drafts"]));
        _list.Children.Add(_drafts);

        _list.Children.Add(Ui.Heading(l["studio.captions.new"]));
        _language = new ComboBox
        {
            Header = l["studio.captions.language"].Replace("_", "", StringComparison.Ordinal),
            ItemsSource = CaptionsViewModel.Languages.Select(x => l[CaptionsViewModel.LanguageKey(x)]).ToList(),
            SelectedIndex = 0,
        };
        _language.SelectionChanged += (_, _) => _vm.Language = CaptionsViewModel.Languages[Math.Max(0, _language.SelectedIndex)];
        _list.Children.Add(_language);

        var cards = new Grid { ColumnSpacing = 12 };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var serverCard = Ui.Card();
        serverCard.Children.Add(new TextBlock { Text = l["studio.captions.server.title"], Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        serverCard.Children.Add(Ui.Text(l["studio.captions.server.body"], dim: true));
        serverCard.Children.Add(_minutes);
        _server = Ui.Button(l["studio.captions.server.start"], () => _ = _vm.StartServerJobAsync(), accent: true);
        AutomationProperties.SetAutomationId(_server, "CaptionsServerStart");
        serverCard.Children.Add(_server);
        cards.Children.Add(serverCard);

        _device.Children.Add(new TextBlock { Text = l["studio.captions.device.title"], Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        _device.Children.Add(Ui.Text(l["studio.captions.device.body"], dim: true));
        _model = new ComboBox { Header = l["studio.captions.device.model"].Replace("_", "", StringComparison.Ordinal), MinWidth = 280 };
        _model.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var i = _model.SelectedIndex;
            if (i >= 0 && i < _modelIds.Count) _vm.Model = _modelIds[i];
        };
        _device.Children.Add(_model);
        var deviceActions = Ui.Row();
        _transcribe = Ui.Button(l["studio.captions.device.start"], () => _ = PickAndTranscribeAsync());
        _cancelDevice = Ui.Button(l["common.cancel"], () => _vm.CancelOnDevice());
        deviceActions.Children.Add(_transcribe);
        deviceActions.Children.Add(_cancelDevice);
        _device.Children.Add(deviceActions);
        AutomationProperties.SetName(_progress, l["studio.captions.device.title"]);
        _device.Children.Add(_progress);
        _device.Children.Add(_progressText);
        Grid.SetColumn(_device, 1);
        cards.Children.Add(_device);
        _list.Children.Add(cards);

        _list.Children.Add(Ui.Heading(l["studio.captions.jobs"]));
        _list.Children.Add(_jobs);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CaptionsViewModel.Editor)) ShowEditor();
            else Render();
        };
        _vm.RefreshModels();
        Render();
    }

    public FrameworkElement Root { get; }

    /// <summary>CI screenshots: open the first draft in the editor.</summary>
    public void RunScene(string scene)
    {
        if (scene == "captions-editor" && _vm.Drafts?.Drafts.FirstOrDefault() is { } d) _ = _vm.OpenDraftAsync(d.Language);
    }

    private void Render()
    {
        _syncing = true;
        try
        {
            _videoIds = _vm.Videos.Select(v => v.Id).ToList();
            _video.ItemsSource = _vm.Videos.Select(v => v.Title).ToList();
            _video.SelectedIndex = _vm.Video == null ? -1 : _videoIds.ToList().IndexOf(_vm.Video.Id);
            _modelIds = _vm.Models.Select(m => m.Id).ToList();
            _model.ItemsSource = _vm.Models.Select(m => _l.Format(m.Installed ? "studio.captions.device.installed" : "studio.captions.device.download", Localizer.Isolate(m.Id), Localizer.Isolate(Size(m.SizeBytes)))).ToList();
            _model.SelectedIndex = _vm.Model == null ? -1 : _modelIds.ToList().IndexOf(_vm.Model);
        }
        finally
        {
            _syncing = false;
        }

        _status.Text = _vm.IsLoading ? _l["studio.shell.loading"] : _vm.Videos.Count == 0 ? _l["studio.captions.noVideos"] : "";
        Ui.Show(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Show(_notice, _vm.NoticeKey is { } n ? _l[n] : null);

        _drafts.Children.Clear();
        var drafts = _vm.Drafts?.Drafts ?? Array.Empty<CaptionDraftSummary>();
        if (drafts.Count == 0) _drafts.Children.Add(Ui.Text(_l["studio.captions.noDrafts"], dim: true));
        foreach (var d in drafts) _drafts.Children.Add(DraftRow(d));
        foreach (var c in _vm.Drafts?.Claims ?? Array.Empty<CaptionClaim>())
        {
            _drafts.Children.Add(Ui.Text(_l.Format(c.Status == CaptionClaimStatus.Failed ? "studio.captions.claimFailed" : "studio.captions.claimQueued", _l[CaptionsViewModel.LanguageKey(c.Language)]), dim: true));
        }

        if (_vm.Minutes is { } m)
        {
            _minutes.Text = m.Unlimited ? _l["studio.captions.minutes.unlimited"]
                : m.HasNoPlan ? _l["studio.captions.minutes.none"]
                : m.MinutesNeeded is { } need ? _l.Format("studio.captions.minutes.needed", _l.Number((long)Math.Ceiling(need)), _l.Number((long)Math.Floor(m.Remaining ?? 0)))
                : _l.Format("studio.captions.minutes.left", _l.Number((long)Math.Floor(m.Remaining ?? 0)));
        }

        _server.IsEnabled = _vm.Video != null && _vm.Minutes?.CanStart == true && !_vm.Jobs.Any(j => j.IsActive);

        _device.Visibility = _vm.OnDeviceAvailable ? Visibility.Visible : Visibility.Collapsed;
        var busy = _vm.IsBusyOnDevice;
        _transcribe.IsEnabled = !busy && _vm.Video != null && _vm.Model != null;
        _model.IsEnabled = !busy;
        _cancelDevice.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = _vm.Progress;
        _progressText.Text = _vm.Stage switch
        {
            OnDeviceStage.Downloading => _l.Format("studio.captions.device.downloading", _l.Number((long)Math.Round(_vm.Progress * 100))),
            OnDeviceStage.Transcribing => _l.Format("studio.captions.device.transcribing", _l.Number((long)Math.Round(_vm.Progress * 100))),
            OnDeviceStage.Sending => _l["studio.captions.device.sending"],
            OnDeviceStage.Waiting => _l["studio.captions.device.waiting"],
            _ => "",
        };

        _jobs.Children.Clear();
        if (_vm.Jobs.Count == 0) _jobs.Children.Add(Ui.Text(_l["studio.captions.noJobs"], dim: true));
        foreach (var j in _vm.Jobs)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lang = _l[CaptionsViewModel.LanguageKey(j.Language)];
            var text = Ui.Text(_l.Format("studio.captions.jobRow", lang, _l["studio.captions.job." + JobKey(j.Status)]));
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            if (j.Fraction is { } f && j.IsActive)
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = f, Width = 160, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(bar, text.Text);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
            }

            if (j.IsActive)
            {
                var cancel = Ui.Button(_l["common.cancel"], () => _ = _vm.CancelJobAsync(j.Id), name: _l.Format("studio.captions.cancelJob", lang));
                Grid.SetColumn(cancel, 2);
                row.Children.Add(cancel);
            }

            _jobs.Children.Add(row);
        }
    }

    private FrameworkElement DraftRow(CaptionDraftSummary d)
    {
        var card = new Grid
        {
            ColumnSpacing = 12,
            Padding = new Thickness(16, 12, 16, 12),
            Background = Ui.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        var lang = _l[CaptionsViewModel.LanguageKey(d.Language)];
        text.Children.Add(new TextBlock { Text = lang, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var state = d.IsOutdated ? _l["studio.captions.state.outdated"] : d.IsPublished ? _l["studio.captions.state.published"] : _l["studio.captions.state.draft"];
        text.Children.Add(Ui.Text(_l.Format("studio.captions.draftLine", _l[CaptionJson.OriginKey(d.Origin)], _l.Number(d.CueCount), state), dim: true));
        if (d.PendingQuran > 0) text.Children.Add(Ui.Text(_l.Format("studio.captions.pendingQuran", _l.Number(d.PendingQuran)), dim: true));
        card.Children.Add(text);
        var edit = Ui.Button(_l["studio.captions.edit"], () => _ = _vm.OpenDraftAsync(d.Language), name: _l.Format("studio.captions.editLang", lang));
        edit.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(edit, 1);
        card.Children.Add(edit);
        return card;
    }

    private static string JobKey(CaptionJobStatus s) => s switch
    {
        CaptionJobStatus.Queued => "queued",
        CaptionJobStatus.WaitingBudget => "waiting",
        CaptionJobStatus.Extracting => "extracting",
        CaptionJobStatus.Transcribing => "transcribing",
        CaptionJobStatus.Postprocessing => "postprocessing",
        CaptionJobStatus.Ready => "ready",
        CaptionJobStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    private string Size(long bytes) => bytes >= 1_000_000_000 ? _l.Format("studio.size.gb", _l.Decimal(bytes / 1e9)) : _l.Format("studio.size.mb", _l.Number((long)Math.Round(bytes / 1e6)));

    private async Task PickAndTranscribeAsync()
    {
        if (await _pickMedia() is { } path) await _vm.TranscribeOnDeviceAsync(path);
    }

    private void ShowEditor()
    {
        _host.Children.Clear();
        if (_vm.Editor is { } editor) _host.Children.Add(new CueEditorView(_l, editor).Root);
        else _host.Children.Add(_list);
    }
}

/// <summary>The cue editor: one card per cue with times, text, and split, merge, nudge and delete.</summary>
internal sealed class CueEditorView
{
    private readonly Localizer _l;
    private readonly CueEditorViewModel _vm;
    private readonly StackPanel _cues = new() { Spacing = 8 };
    private readonly TextBlock _title = new() { Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _error = Ui.Bar(InfoBarSeverity.Error);
    private readonly InfoBar _notice = Ui.Bar(InfoBarSeverity.Success);
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _save;
    private readonly Button _publish;
    private object? _renderedList;
    private int _renderedCount = -1;
    private bool _typingOnly;

    public CueEditorView(Localizer l, CueEditorViewModel vm)
    {
        _l = l;
        _vm = vm;
        var outer = new StackPanel { Spacing = 10, Padding = new Thickness(20) };
        Root = outer;
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Ui.Button(l["studio.captions.editor.back"], () => _ = _vm.CloseAsync());
        bar.Children.Add(back);
        Grid.SetColumn(_title, 1);
        bar.Children.Add(_title);
        var buttons = Ui.Row();
        _undo = Ui.Button(l["studio.captions.editor.undo"], () => _vm.Undo());
        _redo = Ui.Button(l["studio.captions.editor.redo"], () => _vm.Redo());
        _save = Ui.Button(l["studio.captions.editor.save"], () => _ = _vm.SaveAsync());
        _publish = Ui.Button(l["studio.captions.editor.publish"], () => _ = _vm.PublishAsync(), accent: true);
        var delete = Ui.Button(l["studio.captions.editor.delete"], () => _ = _vm.DeleteDraftAsync());
        delete.Foreground = Ui.Brush("SystemFillColorCriticalBrush");
        foreach (var b in new[] { _undo, _redo, _save, _publish, delete }) buttons.Children.Add(b);
        Grid.SetColumn(buttons, 2);
        bar.Children.Add(buttons);
        outer.Children.Add(bar);
        outer.Children.Add(Ui.Text(l["studio.captions.editor.hint"], dim: true));
        outer.Children.Add(_error);
        outer.Children.Add(_notice);
        outer.Children.Add(_cues);

        Accelerator(outer, VirtualKey.Z, VirtualKeyModifiers.Control, () => _vm.Undo());
        Accelerator(outer, VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _vm.Redo());
        Accelerator(outer, VirtualKey.Y, VirtualKeyModifiers.Control, () => _vm.Redo());
        Accelerator(outer, VirtualKey.S, VirtualKeyModifiers.Control, () => _ = _vm.SaveAsync());

        _vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    public FrameworkElement Root { get; }

    private static void Accelerator(UIElement target, VirtualKey key, VirtualKeyModifiers modifiers, Action act)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        a.Invoked += (_, e) =>
        {
            act();
            e.Handled = true;
        };
        target.KeyboardAccelerators.Add(a);
    }

    private void Render()
    {
        var s = _vm.Summary;
        _title.Text = _l.Format("studio.captions.editor.title", _l[CaptionsViewModel.LanguageKey(s.Language)], _l[CaptionJson.OriginKey(s.Origin)]);
        _undo.IsEnabled = _vm.CanUndo;
        _redo.IsEnabled = _vm.CanRedo;
        _save.IsEnabled = _vm.IsDirty && !_vm.IsSaving;
        _publish.IsEnabled = !_vm.IsSaving;
        Ui.Show(_error, _vm.ErrorKey is { } e ? _l[e] : null);
        Ui.Show(_notice, _vm.NoticeKey is { } n && !_vm.IsDirty ? _l[n] : null);

        // Rebuilding while typing would steal focus: only rebuild when the list changed other than by typing.
        var list = _vm.Cues;
        if (ReferenceEquals(list, _renderedList)) return;
        var structural = list.Count != _renderedCount || !_typingOnly;
        _renderedList = list;
        _renderedCount = list.Count;
        if (!structural) return;
        _cues.Children.Clear();
        for (var i = 0; i < list.Count; i++) _cues.Children.Add(CueRow(i, list[i]));
    }

    private FrameworkElement CueRow(int index, CaptionCue cue)
    {
        var card = Ui.Card();
        var n = _l.Number(index + 1);
        // The row follows the reading direction: start, then end.
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        times.Children.Add(new TextBlock { Text = n, Width = 28, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6), Foreground = Ui.Brush("TextFillColorSecondaryBrush") });
        var start = TimeBox(_l["studio.captions.editor.start"], _l.Format("studio.captions.editor.startOf", n), cue.StartMs);
        var end = TimeBox(_l["studio.captions.editor.end"], _l.Format("studio.captions.editor.endOf", n), cue.EndMs);
        void Commit()
        {
            var a = CueEditorViewModel.ParseTime(start.Text);
            var b = CueEditorViewModel.ParseTime(end.Text);
            if (a is int s && b is int e && (s != cue.StartMs || e != cue.EndMs)) _vm.SetTimes(index, s, e);
        }

        foreach (var box in new[] { start, end })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                Commit();
                e.Handled = true;
            };
            box.LostFocus += (_, _) => Commit();
        }

        times.Children.Add(Nudge(_l["studio.captions.editor.minus"], _l.Format("studio.captions.editor.startEarlier", n), () => _vm.Nudge(index, CueEditing.Edge.Start, -CueEditing.NudgeMs)));
        times.Children.Add(start);
        times.Children.Add(Nudge(_l["studio.captions.editor.plus"], _l.Format("studio.captions.editor.startLater", n), () => _vm.Nudge(index, CueEditing.Edge.Start, CueEditing.NudgeMs)));
        // Start to end follows the reading direction: the arrow points left in Arabic.
        times.Children.Add(new TextBlock { Text = _l.IsRtl ? "←" : "→", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 4, 6) });
        times.Children.Add(Nudge(_l["studio.captions.editor.minus"], _l.Format("studio.captions.editor.endEarlier", n), () => _vm.Nudge(index, CueEditing.Edge.End, -CueEditing.NudgeMs)));
        times.Children.Add(end);
        times.Children.Add(Nudge(_l["studio.captions.editor.plus"], _l.Format("studio.captions.editor.endLater", n), () => _vm.Nudge(index, CueEditing.Edge.End, CueEditing.NudgeMs)));
        card.Children.Add(times);

        TextBox? firstText = null;
        var firstSegment = -1;
        for (var si = 0; si < cue.Segments.Count; si++)
        {
            var seg = cue.Segments[si];
            if (seg.IsQuran)
            {
                card.Children.Add(Ui.Text(_l.Format("studio.captions.editor.quran", seg.QuranRange ?? ""), dim: true));
                continue;
            }

            var box = new TextBox
            {
                Header = _l["studio.captions.editor.text"],
                Text = seg.Speech ?? "",
                FlowDirection = _vm.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
            };
            AutomationProperties.SetName(box, _l.Format("studio.captions.editor.textOf", n));
            var segIndex = si;
            box.TextChanged += (_, _) =>
            {
                _typingOnly = true;
                try
                {
                    _vm.SetText(index, segIndex, box.Text);
                }
                finally
                {
                    _typingOnly = false;
                }
            };
            card.Children.Add(box);
            if (firstText == null)
            {
                firstText = box;
                firstSegment = si;
            }
        }

        var actions = Ui.Row(6);
        if (firstText != null)
        {
            var text = firstText;
            var seg = firstSegment;
            actions.Children.Add(Ui.Button(_l["studio.captions.editor.split"], () =>
            {
                if (!_vm.Split(index, seg, text.SelectionStart)) _vm.ErrorKey = "studio.captions.editor.cantSplit";
            }, name: _l.Format("studio.captions.editor.splitOf", n)));
        }

        if (index + 1 < _vm.Cues.Count)
        {
            actions.Children.Add(Ui.Button(_l["studio.captions.editor.merge"], () =>
            {
                if (!_vm.MergeWithNext(index)) _vm.ErrorKey = "studio.captions.editor.cantMerge";
            }, name: _l.Format("studio.captions.editor.mergeOf", n)));
        }

        actions.Children.Add(Ui.Button(_l["studio.captions.editor.deleteCue"], () => _vm.Delete(index), name: _l.Format("studio.captions.editor.deleteCueOf", n)));
        card.Children.Add(actions);
        return card;
    }

    private static TextBox TimeBox(string header, string name, int ms)
    {
        var box = new TextBox { Header = header, Text = CueEditorViewModel.Time(ms), Width = 120 };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static Button Nudge(string symbol, string name, Action act)
    {
        var b = Ui.Button(symbol, act, name: name);
        b.VerticalAlignment = VerticalAlignment.Bottom;
        ToolTipService.SetToolTip(b, name);
        return b;
    }
}
