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
