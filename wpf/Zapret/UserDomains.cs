// Custom domains: the one-click "add my site to the bypass" feature.
// Writes into list-general-user.txt (the file every shipped strategy already
// loads via --hostlist) and can trigger a winws restart so the change lands
// without a panel reboot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ydp.Zapret
{
    public static class UserDomains
    {
        /// <summary>Normalize what the user typed into a domain line.
        /// Accepts "example.com", "https://example.com/path", "*.example.com",
        /// full URLs with ports; strips scheme/path/port, lowercases.</summary>
        public static string Normalize(string raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) return null;
            // strip scheme
            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) value = value.Substring(scheme + 3);
            // strip path
            int slash = value.IndexOf('/');
            if (slash >= 0) value = value.Substring(0, slash);
            // strip port
            int colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon && !value.Contains("::")) value = value.Substring(0, colon);
            // strip userinfo (rare but possible)
            int at = value.IndexOf('@');
            if (at >= 0) value = value.Substring(at + 1);
            value = value.Trim().TrimEnd('.').ToLowerInvariant();
            if (value.Length == 0) return null;
            // wildcards allowed (winws hostlist supports leading *.)
            if (value.Contains("*") && !value.StartsWith("*.", StringComparison.Ordinal)) return null;
            // must contain at least one dot and only legal chars
            if (!value.Contains(".")) return null;
            foreach (var ch in value)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '*')) return null;
            }
            return value;
        }

        public static string ListPath(string rootDir)
        {
            return Path.Combine(rootDir, "lists", "list-general-user.txt");
        }

        public static List<string> ReadAll(string rootDir)
        {
            var path = ListPath(rootDir);
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Add domains; returns how many were actually added (not duplicates).</summary>
        public static int Add(string rootDir, IEnumerable<string> domains, out string error)
        {
            error = null;
            var normalized = new List<string>();
            foreach (var d in domains ?? Enumerable.Empty<string>())
            {
                var n = Normalize(d);
                if (n == null) { error = "Некорректный адрес: " + d; return 0; }
                if (!normalized.Contains(n, StringComparer.OrdinalIgnoreCase)) normalized.Add(n);
            }
            if (normalized.Count == 0) { error = "Пустой список"; return 0; }

            var existing = ReadAll(rootDir);
            var toAdd = normalized.Where(n => !existing.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (toAdd.Count == 0) return 0; // all duplicates - not an error

            var path = ListPath(rootDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string>();
            if (File.Exists(path))
            {
                var old = File.ReadAllLines(path);
                // keep trailing newline structure simple
                lines.AddRange(old);
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            }
            lines.AddRange(toAdd);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return toAdd.Count;
        }

        /// <summary>Remove exact domains (normalized comparison). Returns count removed.</summary>
        public static int Remove(string rootDir, string domain)
        {
            var n = Normalize(domain);
            if (n == null) return 0;
            var path = ListPath(rootDir);
            if (!File.Exists(path)) return 0;
            var lines = File.ReadAllLines(path).ToList();
            var kept = lines.Where(l => !string.Equals(l.Trim(), n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == lines.Count) return 0;
            File.WriteAllLines(path, kept, new UTF8Encoding(false));
            return lines.Count - kept.Count;
        }
    }
}


