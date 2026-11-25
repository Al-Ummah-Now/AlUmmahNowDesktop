using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AlUmmahNow.App.Updates
{
    /// <summary>What the update check keeps between runs. No account, no device.</summary>
    public sealed class UpdateMemory
    {
        /// <summary>The server's switch at the last check: Settings → Updates is there before this run's first one.</summary>
        public bool Enabled { get; set; }

        public DateTimeOffset? LastChecked { get; set; }

        /// <summary>
        /// The versions whose bar was closed, newest last. Here and not in the core, which answers "nothing" for a closed
        /// version: Settings still tells of it.
        /// </summary>
        public List<string>? Dismissed { get; set; }

        /// <summary>The core's own state (opaque).</summary>
        public string? Core { get; set; }
    }

    /// <summary>One small JSON file beside the settings. Corrupt, missing or not writable: it starts anew.</summary>
    public sealed class UpdateStore
    {
        private readonly string? _path;

        /// <param name="path">Null keeps nothing (review frames).</param>
        public UpdateStore(string? path) => _path = path;

        public UpdateMemory Load()
        {
            try
            {
                return _path != null && File.Exists(_path)
                    ? JsonSerializer.Deserialize<UpdateMemory>(File.ReadAllText(_path)) ?? new UpdateMemory()
                    : new UpdateMemory();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return new UpdateMemory();
            }
        }

        /// <returns>False when it could not be written: the next run only asks again.</returns>
        public bool Save(UpdateMemory memory)
        {
            if (_path == null) return true;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(memory));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return false;
            }
        }
    }
}
