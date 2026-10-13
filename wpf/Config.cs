using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ydp
{
/// <summary>JSON-file config store (same file layout as the Node build).</summary>
    public static class Config
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    _path = System.IO.Path.Combine(dir, "YoutubeDiscordPanel", "config.json");
                }
                return _path;
            }
        }

        public static string Get(string key, string fallback)
        {
            lock (Gate)
            {
                try
                {
                    var map = Load();
                    string value;
                    if (map.TryGetValue(key, out value) && value != null) return value;
                }
                catch { /* unreadable config must never break the panel */ }
                return fallback;
            }
        }

        public static void Set(string key, string value)
        {
            lock (Gate)
            {
                try
                {
                    var map = Load();
                    map[key] = value;
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    // Minimal, dependency-free writer: the config is a flat map of
                    // string -> string, so System.Text.Json would drag a package
                    // dependency into a 1-3 MB build for nothing.
                    var lines = new System.Text.StringBuilder();
                    lines.Append("{\n");
                    int i = 0;
                    foreach (var kv in map)
                    {
                        lines.Append("  ").Append(Quote(kv.Key)).Append(": ").Append(Quote(kv.Value));
                        if (++i < map.Count) lines.Append(",");
                        lines.Append("\n");
                    }
                    lines.Append("}\n");
                    File.WriteAllText(Path, lines.ToString());
                }
                catch { /* persisting config must never break the panel */ }
            }
        }

        private static System.Collections.Generic.Dictionary<string, string> Load()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(Path)) return map;
            string raw = File.ReadAllText(Path);
            int i = 0;
            while (i < raw.Length)
            {
                int keyStart = raw.IndexOf('"', i);
                if (keyStart < 0) break;
                int keyEnd = raw.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = raw.Substring(keyStart + 1, keyEnd - keyStart - 1);
                int colon = raw.IndexOf(':', keyEnd);
                if (colon < 0) break;
                int valStart = raw.IndexOf('"', colon);
                if (valStart < 0) break;
                int valEnd = raw.IndexOf('"', valStart + 1);
                if (valEnd < 0) break;
                map[Unescape(key)] = Unescape(raw.Substring(valStart + 1, valEnd - valStart - 1));
                i = valEnd + 1;
            }
            return map;
        }

        private static string Quote(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (var ch in s ?? string.Empty)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Unescape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char next = s[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else sb.Append(next);
                        }
                        else sb.Append(next);
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }
    }
}