using System.Text.Json;
using AlUmmahNow.CoreBridge.Logging;

namespace AlUmmahNow.Studio.Upload;

/// <summary>
/// What is still to be sent of one account's finished uploads, kept on disk. The page adds, and what sends them takes away,
/// on threads of their own.
/// </summary>
public sealed class FinishSteps
{
    private static readonly Logger Logger = Log.For("upload");

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, FinishStep> _steps = new();
    private bool _isClosed;

    /// <param name="path">Null keeps nothing.</param>
    public FinishSteps(string? path)
    {
        _path = path;
        try
        {
            if (path != null && File.Exists(path)) _steps = JsonSerializer.Deserialize<Dictionary<string, FinishStep>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            Logger.Warn("saved finish steps unreadable", ex);
        }
    }

    public FinishStep? Get(string id)
    {
        lock (_gate) return _steps.GetValueOrDefault(id);
    }

    public void Add(string id, FinishStep step)
    {
        lock (_gate) _steps[id] = step;
    }

    public bool Remove(string id)
    {
        lock (_gate) return _steps.Remove(id);
    }

    /// <summary>One more attempt that failed; how many there were.</summary>
    public int Failed(FinishStep step, string error)
    {
        lock (_gate)
        {
            step.LastError = error;
            return ++step.Attempts;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            if (_path == null || _isClosed) return;
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(_steps));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Warn("finish steps not saved", ex);
                try
                {
                    File.Delete(temp);
                }
                catch (Exception left) when (left is IOException || left is UnauthorizedAccessException)
                {
                    // Left behind: nothing reads it
                }
            }
        }
    }

    /// <summary>Saved for the last time: the file is the next start's from now on. What is still on its way is sent again then.</summary>
    public void Close()
    {
        lock (_gate)
        {
            Save();
            _isClosed = true;
        }
    }
}
