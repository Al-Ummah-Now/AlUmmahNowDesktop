using System;
using System.IO;
using System.Text;

namespace AlUmmahNow.CoreBridge.Logging
{
    /// <summary>
    /// Appends to a UTF-8 file and rolls it over at <c>maxBytes</c>, keeping <c>keep</c> old files
    /// (<c>name.1.log</c> is the newest). Thread-safe.
    /// </summary>
    public sealed class FileLogSink : ILogSink
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _keep;

        public FileLogSink(string path, long maxBytes = 2 * 1024 * 1024, int keep = 3)
        {
            _path = path;
            _maxBytes = maxBytes;
            _keep = Math.Max(1, keep);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public string Path => _path;

        public void Write(LogEntry entry)
        {
            var line = entry + Environment.NewLine;
            lock (_gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length + Encoding.UTF8.GetByteCount(line) > _maxBytes) Roll();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }

        private void Roll()
        {
            var stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path) ?? "", System.IO.Path.GetFileNameWithoutExtension(_path));
            var ext = System.IO.Path.GetExtension(_path);
            var oldest = stem + "." + _keep + ext;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var i = _keep - 1; i >= 1; i--)
            {
                var from = stem + "." + i + ext;
                if (File.Exists(from)) File.Move(from, stem + "." + (i + 1) + ext);
            }

            File.Move(_path, stem + ".1" + ext);
        }
    }

    /// <summary>Writes to a text writer: stderr on Linux (journald picks it up), test output.</summary>
    public sealed class TextWriterLogSink : ILogSink
    {
        private readonly TextWriter _writer;
        private readonly object _gate = new object();

        public TextWriterLogSink(TextWriter writer) => _writer = writer;

        /// <summary>The process's standard error. The only place in src/ allowed to touch the console.</summary>
        public static TextWriterLogSink StandardError() => new TextWriterLogSink(Console.Error);

        public void Write(LogEntry entry)
        {
            lock (_gate)
            {
                _writer.WriteLine(entry.ToString());
                _writer.Flush();
            }
        }
    }
}
