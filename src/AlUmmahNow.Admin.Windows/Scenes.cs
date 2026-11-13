using AlUmmahNow.Admin.Review;

namespace AlUmmahNow.Admin.WindowsApp;

/// <summary>
/// CI scenarios on the demo queue (AUN_DEMO=1): each drives the real window the way a reviewer
/// would and says what it saw; the workflow also captures the screen and the UI Automation tree.
/// </summary>
internal static class Scenes
{
    public static async Task<string> RunAsync(AdminWindow w, string scene)
    {
        switch (scene)
        {
            case "queue":
                return "rows=" + string.Join(",", w.Queue.Items.Select(i => i.Id));
            case "history":
                w.Queue.Tab = ReviewTab.History;
                await w.Queue.RefreshAsync();
                return "history=" + w.Queue.Items.Count;
            case "search":
                await w.Queue.SearchAsync("surah");
                await Task.Delay(ReviewQueue.SearchDelay + TimeSpan.FromMilliseconds(200));
                return "search=" + string.Join(",", w.Queue.Items.Select(i => i.Id));
            case "detail":
                await w.OpenAsync("rev-001");
                return "detail=" + w.Session.Detail?.Item.Id + " canDecide=" + w.Session.CanDecide;
            case "play":
                await w.OpenAsync("rev-001");
                await Task.Delay(1500);
                await w.RunAsync(ReviewCommand.PlayPause);
                return "play=" + w.Session.Detail?.Stream?.Url?.Host;
            case "blurred":
                await w.OpenAsync("rev-002");
                return "blurred=" + !w.Session.Reveal.IsShown("rev-002", true);
            case "revealed":
                await w.OpenAsync("rev-002");
                var shown = await w.ShowAsync();
                return "revealed=" + (shown && w.Session.Reveal.IsShown("rev-002", true));
            case "hidden":
                await w.OpenAsync("rev-002");
                await w.ShowAsync();
                w.OnHidden();
                return "hiddenReblurred=" + !w.Session.Reveal.IsShown("rev-002", true);
            case "approve":
                await w.OpenAsync("rev-001");
                var approved = await w.DecideAsync(ReviewDecision.Approve);
                return "approve=" + approved?.Kind;
            case "undo":
                await w.OpenAsync("rev-001");
                await w.DecideAsync(ReviewDecision.Approve);
                await w.UndoAsync();
                return "undo=" + (w.Queue.Find("rev-001")?.IsOpen == true);
            case "conflict":
                await w.OpenAsync("rev-005");
                var conflict = await w.DecideAsync(ReviewDecision.Reject, "spam_scam");
                return "conflict=" + conflict?.Kind + " by=" + conflict?.Conflict?.ByName;
            case "reason":
                await w.OpenAsync("rev-001");
                // Opens the reason dialog and leaves it up for the screenshot.
                _ = w.RunAsync(ReviewCommand.Reject);
                return "reason=open";
            case "keys":
                _ = w.RunAsync(ReviewCommand.Help);
                return "keys=open";
            case "notstaff":
            case "locked":
            default:
                return scene;
        }
    }

    public static bool Passed(AdminWindow w, string scene, string result) => scene switch
    {
        "" => w.State == AdminState.Ready,
        "queue" => result == "rows=rev-002,rev-001,rev-004,rev-005,rev-003,rev-006",
        "history" => result == "history=2",
        "search" => result == "search=rev-003",
        "detail" => result == "detail=rev-001 canDecide=True",
        "play" => result.StartsWith("play=127.0.0.1", StringComparison.Ordinal),
        "blurred" => result == "blurred=True",
        "revealed" => result == "revealed=True",
        "hidden" => result == "hiddenReblurred=True",
        "approve" => result == "approve=Done" && w.Queue.Find("rev-001")?.IsOpen == false,
        "undo" => result == "undo=True",
        "conflict" => result == "conflict=Conflict by=Yusuf Admin",
        "reason" => result == "reason=open",
        "keys" => result == "keys=open",
        "notstaff" => w.State == AdminState.NotStaff,
        "locked" => w.State == AdminState.Locked,
        _ => false,
    };
}
