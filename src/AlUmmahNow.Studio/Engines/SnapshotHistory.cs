namespace AlUmmahNow.Studio.Engines;

/// <summary>
/// Undo and redo over whole snapshots (the editor keeps project JSON, the caption editor its cues). Bounded so a
/// long session never grows without limit; recording a new state clears the redo side.
/// </summary>
public sealed class SnapshotHistory<T>
{
    private readonly List<T> _undo = new();
    private readonly List<T> _redo = new();
    private readonly IEqualityComparer<T> _eq;

    public SnapshotHistory(T initial, int limit = 200, IEqualityComparer<T>? comparer = null)
    {
        Current = initial;
        Limit = Math.Max(1, limit);
        _eq = comparer ?? EqualityComparer<T>.Default;
    }

    public T Current { get; private set; }
    public int Limit { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every change of <see cref="Current"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Makes <paramref name="state"/> current. A state equal to the current one is not an edit.</summary>
    public void Record(T state)
    {
        if (_eq.Equals(state, Current)) return;
        _undo.Add(Current);
        if (_undo.Count > Limit) _undo.RemoveRange(0, _undo.Count - Limit);
        _redo.Clear();
        Current = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the current state without a new undo step (typing in the same field keeps one step).</summary>
    public void Amend(T state)
    {
        if (!CanUndo)
        {
            Record(state);
            return;
        }

        Current = state;
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Starts over from <paramref name="state"/> (a reload from the server), dropping both sides.</summary>
    public void Reset(T state)
    {
        Current = state;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
