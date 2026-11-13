namespace AlUmmahNow.Admin.Media;

/// <summary>
/// Blur for NSFW and minor-present items (wiki AUN-ADMIN-NATIVE, Media handling): blurred by
/// default, shown only after an audited per-item "Show", and blurred again on leaving the item,
/// minimising or locking the app, or after 60 seconds without input.
/// </summary>
public sealed class RevealPolicy
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string? _current;
    private string? _revealed;
    private DateTimeOffset _lastInput;

    public RevealPolicy(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastInput = _time.GetUtcNow();
    }

    /// <summary>The view must redraw: something was blurred or shown.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether <paramref name="itemId"/>'s media is shown clearly right now.</summary>
    public bool IsShown(string itemId, bool sensitive)
    {
        if (!sensitive) return true;
        lock (_gate) return _revealed == itemId && _current == itemId;
    }

    public void Enter(string itemId)
    {
        lock (_gate)
        {
            _current = itemId;
            _revealed = null;
            _lastInput = _time.GetUtcNow();
        }

        Raise();
    }

    /// <summary>Called only after the server recorded the reveal.</summary>
    public void Reveal(string itemId)
    {
        lock (_gate)
        {
            if (_current != itemId) return;
            _revealed = itemId;
            _lastInput = _time.GetUtcNow();
        }

        Raise();
    }

    /// <summary>Keyboard, mouse or touch input in the window.</summary>
    public void Input()
    {
        lock (_gate) _lastInput = _time.GetUtcNow();
    }

    /// <summary>Checked once a second by the window: 60 s idle re-blurs.</summary>
    public void Tick()
    {
        bool changed;
        lock (_gate)
        {
            changed = _revealed != null && _time.GetUtcNow() - _lastInput >= IdleLimit;
            if (changed) _revealed = null;
        }

        if (changed) Raise();
    }

    /// <summary>Minimised, hidden, session locked, or the item was closed.</summary>
    public void Hide() => Clear();

    public void Leave()
    {
        lock (_gate) _current = null;
        Clear();
    }

    private void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _revealed != null;
            _revealed = null;
        }

        if (changed) Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
