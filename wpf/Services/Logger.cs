using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ydp.Services
{
    public sealed class LogEntry
    {
        public string ts { get; set; }
        public string level { get; set; }
        public string source { get; set; }
        public string msg { get; set; }

        public string ToLine()
        {
            return "[" + ts + "] " + level.ToUpperInvariant().PadRight(5) + " " + (source ?? "panel").PadRight(10) + " " + msg;
        }
    }

    /// <summary>
    /// Panel log: a bounded in-memory buffer for the UI plus a rotated file on
    /// disk.
    ///
    /// Bounded on purpose — the Node version kept an unbounded buffer, so a
    /// long tester run (22 strategies x 13 probes) grew memory until the process
    /// felt heavy. 2000 lines is far more than the UI shows and costs nothing.
    /// </summary>
    public static class Logger
    {
        private const int MaxLines = 2000;
        private const long MaxBytes = 5 * 1024 * 1024;

        private static readonly Queue<LogEntry> Buffer = new Queue<LogEntry>();
        private static readonly object Gate = new object();
        private static string _path;
        private static long _written;

        public static event Action<LogEntry> Appended;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YoutubeDiscordPanel");
                    Directory.CreateDirectory(dir);
                    _path = System.IO.Path.Combine(dir, "app.log");
                    RotateIfNeeded();
                }
                return _path;
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length < MaxBytes) return;
                string backup = _path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_path, backup);
                _written = 0;
            }
            catch
            {
                // Rotation is best effort.
            }
        }

        public static void Info(string message, string source = "panel")
        {
            Add("info", message, source);
        }

        public static void Warn(string message, string source = "panel")
        {
            Add("warn", message, source);
        }

        public static void Error(string message, string source = "panel")
        {
            Add("error", message, source);
        }

        public static void Add(string level, string message, string source = "panel")
        {
            var entry = new LogEntry
            {
                ts = DateTime.Now.ToString("HH:mm:ss"),
                level = level,
                source = source,
                msg = message ?? string.Empty,
            };
            lock (Gate)
            {
                Buffer.Enqueue(entry);
                while (Buffer.Count > MaxLines) Buffer.Dequeue();
            }
            Appended?.Invoke(entry);
            AppendToFile(entry);
        }

        private static void AppendToFile(LogEntry entry)
        {
            try
            {
                string path = Path;
                string line = entry.ToLine() + Environment.NewLine;
                File.AppendAllText(path, line, Encoding.UTF8);
                _written += line.Length;
            }
            catch
            {
                // Writing the log must never break the operation that logged.
            }
        }

        public static List<LogEntry> Buffer_()
        {
            lock (Gate) { return new List<LogEntry>(Buffer); }
        }

        public static List<LogEntry> Filtered(string level, string text)
        {
            var all = Buffer_();
            if (!string.IsNullOrEmpty(level) && !string.Equals(level, "all", StringComparison.OrdinalIgnoreCase))
            {
                all = all.FindAll(e => string.Equals(e.level, level, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(text))
            {
                all = all.FindAll(e => (e.msg ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                    || (e.source ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return all;
        }

        public static void Clear()
        {
            lock (Gate) { Buffer.Clear(); }
        }

        /// <summary>Export the visible lines to a file the user picks.</summary>
        public static bool Export(string file, string level, string text)
        {
            try
            {
                var lines = Filtered(level, text);
                var sb = new StringBuilder();
                foreach (var line in lines) sb.Append(line.ToLine()).Append(Environment.NewLine);
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}