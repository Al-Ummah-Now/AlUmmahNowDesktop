namespace AlUmmahNow.Admin.Security;

/// <summary>
/// When the app asks for Windows Hello (or Face ID / biometrics on the other platforms): on open,
/// and when it comes back after 5 minutes or more minimised, hidden or with the session locked.
/// While locked nothing of the queue is on screen.
/// </summary>
public sealed class LockPolicy
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private DateTimeOffset? _hiddenAt;

    public LockPolicy(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        IsLocked = true;
    }

    public bool IsLocked { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Verification passed.</summary>
    public void Unlocked()
    {
        IsLocked = false;
        _hiddenAt = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Minimised, hidden or the Windows session locked. Content is hidden at once either way.</summary>
    public void Hidden() => _hiddenAt ??= _time.GetUtcNow();

    /// <summary>Visible again: returns true when Hello must be asked before content shows.</summary>
    public bool Shown()
    {
        if (_hiddenAt is { } at && _time.GetUtcNow() - at >= Grace && !IsLocked)
        {
            IsLocked = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _hiddenAt = null;
        return IsLocked;
    }

    /// <summary>Sign-out or a switch of account.</summary>
    public void Lock()
    {
        IsLocked = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
