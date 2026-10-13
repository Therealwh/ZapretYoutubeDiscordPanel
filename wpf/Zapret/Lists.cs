using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ydp.Zapret
{
    /// <summary>
    /// ipset/list files that live next to zapret.
    ///
    /// ensureUserLists matters more than it looks: winws treats a missing
    /// --ipset-exclude / --hostlist file as fatal and exits with code 1
    /// ("cannot access ipset file"), and a fresh extraction ships without the
    /// *-user.txt files, so without this every strategy looks like
    /// "не запустилась".
    /// </summary>
    public static class Lists
    {
        public static readonly string[] EditableKeys = { "general", "exclude", "ipsetExclude" };

        private static string UserListPath(string rootDir, string key)
        {
            switch (key)
            {
                case "general": return Path.Combine(rootDir, "lists", "list-general-user.txt");
                case "exclude": return Path.Combine(rootDir, "lists", "list-exclude-user.txt");
                case "ipsetExclude": return Path.Combine(rootDir, "lists", "ipset-exclude-user.txt");
                default: return null;
            }
        }

        private static string BaseListPath(string rootDir, string key)
        {
            switch (key)
            {
                case "general": return Path.Combine(rootDir, "lists", "list-general.txt");
                case "exclude": return Path.Combine(rootDir, "lists", "list-exclude.txt");
                case "ipsetExclude": return Path.Combine(rootDir, "lists", "ipset-exclude.txt");
                default: return null;
            }
        }

        public static void EnsureUserLists(string rootDir)
        {
            foreach (var key in EditableKeys)
            {
                string path = UserListPath(rootDir, key);
                if (path == null) continue;
                if (File.Exists(path)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string header = "# user list — создан панелью, можно править\n"
                                  + "# добавьте по одному имени домена или IP в строке\n";
                    File.WriteAllText(path, header);
                }
                catch
                {
                    // winws will report the real error if it is still missing.
                }
            }
        }

        public static string Read(string rootDir, string key)
        {
            string path = UserListPath(rootDir, key) ?? BaseListPath(rootDir, key);
            if (path == null) return string.Empty;
            try { return File.ReadAllText(path); }
            catch { return string.Empty; }
        }

        public static bool Write(string rootDir, string key, string content)
        {
            string path = UserListPath(rootDir, key);
            if (path == null) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public sealed class ListStats
        {
            public int lines { get; set; }
            public int comments { get; set; }
            public int blank { get; set; }
            public int duplicates { get; set; }
            public int invalid { get; set; }
            public int bytes { get; set; }
        }

        public static ListStats Stats(string rootDir, string key)
        {
            string content = Read(rootDir, key);
            var stats = new ListStats { bytes = content.Length };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in content.Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0) { stats.blank++; continue; }
                if (line.StartsWith("#", StringComparison.Ordinal)) { stats.comments++; continue; }
                stats.lines++;
                if (!seen.Add(line)) stats.duplicates++;
                if (!LooksLikeEntry(line)) stats.invalid++;
            }
            return stats;
        }

        private static bool LooksLikeEntry(string line)
        {
            string value = line;
            int slash = value.IndexOf('/');
            if (slash > 0) value = value.Substring(0, slash);
            if (value.Length == 0) return false;
            if (value == "0.0.0.0/0" || value == "::/0") return true;
            // A domain (letters/dots/hyphens) or an IPv4 address.
            bool dots = value.IndexOf('.') >= 0;
            bool digits = value.All(c => char.IsDigit(c) || c == '.' || c == ':' || char.IsLetterOrDigit(c));
            return (dots || digits) && value.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ':' || c == '/');
        }

        /// <summary>Smart-list operations: dedupe, sort, drop comments/blanks.</summary>
        public static bool ApplyOperation(string rootDir, string key, string op)
        {
            string content = Read(rootDir, key);
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            var result = new List<string>();
            switch ((op ?? string.Empty).ToLowerInvariant())
            {
                case "dedupe":
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var line in lines)
                        {
                            string value = line.Trim().TrimEnd('\r');
                            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;
                            if (seen.Add(value)) result.Add(value);
                        }
                        break;
                    }
                case "sort":
                    {
                        var entries = lines.Select(l => l.Trim().TrimEnd('\r'))
                            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
                        result.Add("# user list — создан панелью, можно править");
                        result.AddRange(entries);
                        break;
                    }
                case "cleanup":
                    {
                        var entries = new List<string>();
                        var header = new List<string>();
                        foreach (var line in lines)
                        {
                            string value = line.Trim().TrimEnd('\r');
                            if (value.Length == 0) continue;
                            if (value.StartsWith("#", StringComparison.Ordinal)) header.Add(value);
                            else entries.Add(value);
                        }
                        var deduped = new List<string>();
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in entries) if (seen.Add(entry)) deduped.Add(entry);
                        result.AddRange(header);
                        result.AddRange(deduped);
                        break;
                    }
                default:
                    return false;
            }
            return Write(rootDir, key, string.Join("\r\n", result) + "\r\n");
        }

        /// <summary>Read the upstream ipset-all.txt (used by the ipset filter mode).</summary>
        public static string ReadIpsetAll(string rootDir)
        {
            try { return File.ReadAllText(Path.Combine(rootDir, "lists", "ipset-all.txt")); }
            catch { return string.Empty; }
        }
    }
}