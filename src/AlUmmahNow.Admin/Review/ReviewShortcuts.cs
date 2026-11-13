namespace AlUmmahNow.Admin.Review;

/// <summary>Every review action has a key (no modifiers, so they work with one hand); Narrator reads them as access keys.</summary>
public enum ReviewCommand { None, Next, Previous, Open, Approve, Reject, Escalate, RequestChanges, Undo, Show, PlayPause, Search, Refresh, Back, Help }

public static class ReviewShortcuts
{
    /// <summary>Key name (Windows VirtualKey names) → command. Ctrl+Z is Undo, Ctrl+F search, F5 refresh.</summary>
    public static ReviewCommand For(string key, bool ctrl, bool shift)
    {
        if (ctrl)
        {
            return key switch
            {
                "Z" => ReviewCommand.Undo,
                "F" => ReviewCommand.Search,
                "R" => ReviewCommand.Refresh,
                _ => ReviewCommand.None,
            };
        }

        return key switch
        {
            "J" or "Down" => ReviewCommand.Next,
            "K" or "Up" => ReviewCommand.Previous,
            "Enter" => ReviewCommand.Open,
            "A" => ReviewCommand.Approve,
            "R" => ReviewCommand.Reject,
            "E" => ReviewCommand.Escalate,
            "C" => ReviewCommand.RequestChanges,
            "U" => ReviewCommand.Undo,
            "S" => ReviewCommand.Show,
            "Space" => ReviewCommand.PlayPause,
            "F5" => ReviewCommand.Refresh,
            "Escape" => ReviewCommand.Back,
            "Number191" when shift => ReviewCommand.Help, // "?"
            "F1" => ReviewCommand.Help,
            "Divide" or "Number191" => ReviewCommand.Search, // "/"
            _ => ReviewCommand.None,
        };
    }

    /// <summary>The shortcut list shown by "?" (i18n key, key label).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Help = new[]
    {
        ("admin.key.next", "J / ↓"), ("admin.key.previous", "K / ↑"), ("admin.key.open", "Enter"),
        ("admin.decision.approve", "A"), ("admin.decision.reject", "R"), ("admin.decision.escalate", "E"),
        ("admin.decision.request_changes", "C"), ("admin.undo", "U / Ctrl+Z"), ("admin.media.show", "S"),
        ("admin.key.play", "Space"), ("admin.search", "/ / Ctrl+F"), ("admin.refresh", "F5"), ("admin.key.back", "Esc"),
    };
}
