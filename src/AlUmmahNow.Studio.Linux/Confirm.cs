namespace AlUmmahNow.Studio.Linux;

/// <summary>
/// In-app confirmation (libadwaita AlertDialog), never an OS message box. Cancel is the default and
/// the close response, so Enter and Escape both cancel; the destructive button must be chosen.
/// </summary>
public static class Confirm
{
    public static Task<bool> AskAsync(Gtk.Widget parent, string heading, string body, string confirmLabel, string cancelLabel, bool destructive = true)
    {
        var done = new TaskCompletionSource<bool>();
        var dialog = Adw.AlertDialog.New(heading, body);
        dialog.AddResponse("cancel", cancelLabel);
        dialog.AddResponse("confirm", confirmLabel);
        dialog.SetResponseAppearance("confirm", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
        dialog.SetDefaultResponse("cancel");
        dialog.SetCloseResponse("cancel");
        dialog.OnResponse += (_, args) => done.TrySetResult(args.Response == "confirm");
        dialog.Present(parent);
        return done.Task;
    }
}

/// <summary>Confirmations asked by view models by i18n key: "&lt;key&gt;.heading", "&lt;key&gt;" (body) and "&lt;key&gt;.ok".</summary>
public static class ConfirmKeys
{
    public static Func<string, object[], Task<bool>> For(Gtk.Widget parent, AlUmmahNow.App.I18n.Localizer l) => (key, args) =>
    {
        var shown = args.Select(a => a is string s && s.StartsWith("studio.", StringComparison.Ordinal) ? (object)l[s] : a).ToArray();
        return Confirm.AskAsync(parent, l[key + ".heading"], l.Format(key, shown), l[key + ".ok"], l["common.cancel"]);
    };
}
