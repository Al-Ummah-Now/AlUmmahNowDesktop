namespace AlUmmahNow.Studio.Upload;

/// <summary>
/// Finds new files in a folder by polling, which also works on network shares. A file is
/// reported only once its size is unchanged between two scans, so a file still being copied waits.
/// </summary>
public sealed class FolderWatch
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mts", ".ts",
    };

    private readonly string _folder;
    private readonly Dictionary<string, long> _lastSizes = new();
    private readonly HashSet<string> _reported = new();

    public FolderWatch(string folder) => _folder = folder;

    /// <summary>Returns files that became stable since the previous scan.</summary>
    public IReadOnlyList<(string Path, long Size)> Scan()
    {
        var ready = new List<(string, long)>();
        if (!Directory.Exists(_folder)) return ready;
        foreach (var path in Directory.EnumerateFiles(_folder).Where(p => VideoExtensions.Contains(Path.GetExtension(p))))
        {
            if (_reported.Contains(path)) continue;
            var size = new FileInfo(path).Length;
            if (size > 0 && _lastSizes.TryGetValue(path, out var previous) && previous == size)
            {
                _reported.Add(path);
                ready.Add((path, size));
            }

            _lastSizes[path] = size;
        }

        return ready;
    }
}
