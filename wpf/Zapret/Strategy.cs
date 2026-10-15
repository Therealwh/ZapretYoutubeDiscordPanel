using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ydp.Zapret
{
    public sealed class StrategyInfo
    {
        public string file { get; set; }
        public string name { get; set; }
        public string title { get; set; }
        public string[] badges { get; set; } = new string[0];
        public string sortKey { get; set; }
    }

    public sealed class ParsedStrategy
    {
        public List<string> args { get; set; } = new List<string>();
        public string argsString { get; set; } = string.Empty;
    }

    /// <summary>
    /// winws argument parser — a faithful port of the Node implementation, which
    /// itself reproduces service.bat's :service_install parser
    /// (Flowseal/zapret-discord-youtube v1.10.3).
    ///
    /// The critical property is quote-aware tokenizing: `--hostlist="C:\Program
    /// Files\zapret\lists\x.txt"` must stay ONE token, otherwise winws reads the
    /// stray fragment as a comma-separated list and exits with "cannot access
    /// ipset file". That bug is exactly what made the bypass refuse to start
    /// from a folder with a space in its name.
    /// </summary>
    public static class Strategy
    {
        private static readonly string[] ArgsWithValue = { "sni", "host", "altorder" };

        // ---- tokenizing -------------------------------------------------

        /// <summary>
        /// Split like cmd's `for %%i in (!line!)`: on space/tab, comma, semicolon
        /// and '=' — but never inside a quoted string.
        /// </summary>
        public static List<string> CmdTokenize(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            foreach (var ch in line ?? string.Empty)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(ch);
                    continue;
                }
                if (!inQuotes && (char.IsWhiteSpace(ch) || ch == ',' || ch == ';' || ch == '='))
                {
                    if (current.Length > 0) result.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        /// <summary>
        /// Expand cmd %-variables the way `call set` re-expansion does inside
        /// service.bat. Unknown variables stay literal (cmd behaviour) so a
        /// %~n0 fragment cannot swallow the following %BIN%.
        /// </summary>
        public static string ExpandVars(string line, Dictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            var output = new StringBuilder();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '%')
                {
                    int end = line.IndexOf('%', i + 1);
                    if (end >= 0)
                    {
                        string name = line.Substring(i + 1, end - i - 1);
                        string replacement;
                        if (vars.TryGetValue(name, out replacement))
                        {
                            output.Append(replacement);
                            i = end + 1;
                            continue;
                        }
                    }
                }
                output.Append(line[i]);
                i++;
            }
            return output.ToString();
        }

        public static Dictionary<string, string> BuildVarMap(string rootDir)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            vars["BIN"] = Path.Combine(rootDir, "bin") + Path.DirectorySeparatorChar;
            vars["LISTS"] = Path.Combine(rootDir, "lists") + Path.DirectorySeparatorChar;
            // service.bat's load_game_filter sets these before install runs; when
            // the utils file is absent the script defaults are 12/12 (disabled).
            vars["GameFilterTCP"] = "12";
            vars["GameFilterUDP"] = "12";

            string gfFile = Path.Combine(rootDir, "utils", "game_filter.enabled");
            try
            {
                string[] rawLines = File.ReadAllLines(gfFile);
                string mode = "disabled";
                string tcp = null;
                string udp = null;
                foreach (var rawLine in rawLines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    string key = (eq == -1 ? line : line.Substring(0, eq)).Trim().ToLowerInvariant();
                    string val = eq == -1 ? string.Empty : line.Substring(eq + 1).Trim();
                    if (key == "mode") mode = val.Length == 0 ? "all" : val;
                    else if (key == "all") mode = "all";
                    else if (key == "udp" && val.Length == 0) mode = "udp";
                    else if (key == "tcp" && val.Length == 0) mode = "tcp";
                    else if (key == "udp") udp = val;
                    else if (key == "tcp") tcp = val;
                }
                string tcpRange = string.IsNullOrEmpty(tcp) ? "1024-65535" : tcp;
                string udpRange = string.IsNullOrEmpty(udp) ? "1024-65535" : udp;
                if (mode == "all")
                {
                    vars["GameFilterTCP"] = tcpRange;
                    vars["GameFilterUDP"] = udpRange;
                }
                else if (mode == "tcp")
                {
                    vars["GameFilterTCP"] = tcpRange;
                    vars["GameFilterUDP"] = "12";
                }
                else if (mode == "udp")
                {
                    vars["GameFilterTCP"] = "12";
                    vars["GameFilterUDP"] = udpRange;
                }
            }
            catch
            {
                // Absent file -> the defaults above.
            }
            return vars;
        }

        // ---- parsing ----------------------------------------------------

        private static readonly Regex WinwsExe = new Regex(@"winws\.exe", RegexOptions.IgnoreCase);
        private static readonly Regex LeadingNoise = new Regex(@"^(start|/min|min)$", RegexOptions.IgnoreCase);

        public static ParsedStrategy ParseStrategyFile(string file, string rootDir = null)
        {
            // A bare file name ("general (ALT2).bat") must be resolved against the
            // zapret root, never against the process working directory (the exe's
            // own bin folder), otherwise ReadAllLines below looks in the wrong
            // place and every call fails with parse_failed.
            if (!string.IsNullOrEmpty(file) && !Path.IsPathRooted(file) && !string.IsNullOrEmpty(rootDir))
                file = Path.Combine(rootDir, file);
            if (rootDir == null) rootDir = Path.GetDirectoryName(file);
            var vars = BuildVarMap(rootDir);
            string[] rawLines = File.ReadAllLines(file);

            var args = new List<string>();
            bool capture = false;
            int mergeargs = 0; // 0 default | 1 params-args | 2 start param | 3 arg-with-value
            bool firstCapturedLineDone = false;

            foreach (var rawLine in rawLines)
            {
                string line = rawLine;
                if (!capture)
                {
                    if (WinwsExe.IsMatch(line)) capture = true;
                    else continue;
                }
                line = ExpandVars(line, vars);
                if (!firstCapturedLineDone)
                {
                    var match = new Regex(@"winws\.exe""", RegexOptions.IgnoreCase).Match(line);
                    if (match.Success)
                    {
                        line = line.Substring(match.Index + @"winws.exe""".Length);
                        firstCapturedLineDone = true;
                    }
                }
                foreach (var token in CmdTokenize(line))
                {
                    if (token == "^" || token == "^^") continue;

                    if (token.StartsWith("--", StringComparison.Ordinal) && mergeargs != 0) mergeargs = 0;

                    string current = token;
                    if (current.StartsWith("\"", StringComparison.Ordinal))
                    {
                        string stripped = current.Length > 1 && current.EndsWith("\"", StringComparison.Ordinal)
                            ? current.Substring(1, current.Length - 2)
                            : current.Substring(1);
                        bool absolute = Path.IsPathRooted(stripped) || Regex.IsMatch(stripped, @"^[a-zA-Z]:");
                        current = "\"" + (absolute ? stripped : Path.Combine(rootDir, stripped)) + "\"";
                    }

                    if (mergeargs == 1) args[args.Count - 1] = args[args.Count - 1] + "," + current;
                    else if (mergeargs == 3)
                    {
                        args.Add(current);
                        mergeargs = 1;
                    }
                    else args.Add(current);

                    if (current.StartsWith("--", StringComparison.Ordinal)) mergeargs = 2;
                    else if (mergeargs >= 1)
                    {
                        if (mergeargs == 2) mergeargs = 1;
                        string bare = current.Trim('"');
                        if (ArgsWithValue.Any(x => string.Equals(x, bare, StringComparison.OrdinalIgnoreCase))) mergeargs = 3;
                    }
                }
            }

            // Defensive: the parser must never emit launcher tokens.
            while (args.Count > 0 && LeadingNoise.IsMatch(args[0])) args.RemoveAt(0);

            return new ParsedStrategy { args = args, argsString = string.Join(" ", args) };
        }

        /// <summary>
        /// Render args for `sc create ... binPath= "..."`: quotes inside must be
        /// escaped as service.bat does.
        /// </summary>
        public static string ToScArgsString(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(a => a.Replace("\"", "\\\"")));
        }

        /// <summary>
        /// Re-attach a flag's value that cmd-style tokenizing split apart.
        /// `--hostlist` + `"C:\Program Files\...\x.txt"` becomes
        /// `--hostlist="C:\Program Files\...\x.txt"`, which is winws' documented
        /// syntax and survives its own tokenizer.
        /// </summary>
        public static List<string> RejoinFlagValues(IEnumerable<string> args)
        {
            var list = args.ToList();
            var output = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                string token = list[i] ?? string.Empty;
                string next = i + 1 < list.Count ? list[i + 1] : null;
                if (token.StartsWith("--", StringComparison.Ordinal)
                    && !token.Contains("=")
                    && !string.IsNullOrEmpty(next)
                    && !next.StartsWith("--", StringComparison.Ordinal))
                {
                    output.Add(token + "=" + next);
                    i++;
                    continue;
                }
                output.Add(token);
            }
            return output;
        }

        /// <summary>
        /// Quote an argument for a Windows command line.
        ///
        /// The parser already emits winws' documented form
        /// (--hostlist="C:\path with spaces\x.txt"), and the reference passes
        /// that through untouched. Wrapping such an argument AGAIN produced
        /// "--hostlist="C:\path..."" - double quotes - which winws rejects, and
        /// the service died with 1067 while the same strategy worked as a
        /// process. So: anything that already carries quotes is left alone.
        /// </summary>
        public static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 1 && arg.StartsWith("\"", StringComparison.Ordinal) && arg.EndsWith("\"", StringComparison.Ordinal))
            {
                return arg; // Already quoted by the parser — keep it intact.
            }
            if (arg.IndexOf('"') >= 0) return arg; // e.g. --hostlist="C:\a b\x.txt"
            if (arg.IndexOf(' ') < 0) return arg;
            return "\"" + arg + "\"";
        }

        // ---- badges -----------------------------------------------------

        private static readonly Regex DesyncEq = new Regex(@"^--dpi-desync=(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex FakeQuic = new Regex(@"(?:^|\s)--dpi-desync-fake-quic(?:=|\s|$)", RegexOptions.IgnoreCase);
        private static readonly Regex FakeTlsMod = new Regex(@"fake-tls-mod=rnd", RegexOptions.IgnoreCase);
        private static readonly Regex FakeTls = new Regex(@"(?:^|\s)--dpi-desync-fake-tls(?:=|\s|$)", RegexOptions.IgnoreCase);
        private static readonly Regex FilterL7 = new Regex(@"(?:^|\s)--filter-l7(?:=|\s)\s*discord", RegexOptions.IgnoreCase);

        /// <summary>
        /// Detect `--dpi-desync=` methods plus notable fakes/filters. Mirrors cmd
        /// tokenizing: '=' is a separator, so `--dpi-desync=fake,multisplit`
        /// arrives as `--dpi-desync` + `fake` + `multisplit`, while a fake
        /// without '=' stays glued to its flag (`--dpi-desync-fake-quic`).
        /// </summary>
        public static List<string> DetectMethods(IEnumerable<string> args)
        {
            var found = new List<string>();
            var list = (args ?? Enumerable.Empty<string>()).Select(a => a ?? string.Empty).ToList();
            Action<string, string> add = (key, value) =>
            {
                if (string.IsNullOrEmpty(value)) return;
                if (!found.Contains(key)) found.Add(key);
            };

            for (int i = 0; i < list.Count; i++)
            {
                var eq = DesyncEq.Match(list[i]);
                bool bare = !eq.Success && string.Equals(list[i], "--dpi-desync", StringComparison.OrdinalIgnoreCase);
                if (!eq.Success && !bare) continue;
                if (eq.Success)
                {
                    foreach (var part in eq.Groups[1].Value.Split(','))
                    {
                        string name = part.Trim().ToLowerInvariant();
                        if (name.Length > 0) add(name, name);
                    }
                }
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[j].StartsWith("--", StringComparison.Ordinal)) break;
                    foreach (var part in list[j].Trim().ToLowerInvariant().Split(','))
                    {
                        string value = part.Trim();
                        if (value.Length > 0) add(value, value);
                    }
                }
            }

            string all = string.Join(" ", list);
            if (FakeQuic.IsMatch(all)) add("fake QUIC", "fake QUIC");
            if (FakeTlsMod.IsMatch(all)) add("auto fake TLS", "auto fake TLS");
            else if (FakeTls.IsMatch(all)) add("fake TLS", "fake TLS");
            if (FilterL7.IsMatch(all)) add("Discord voice/STUN", "Discord voice/STUN");
            return found;
        }

        public static string[] MethodBadges(IEnumerable<string> args)
        {
            var methods = DetectMethods(args);
            var badges = new List<string>();
            if (methods.Contains("multisplit")) badges.Add("multisplit");
            if (methods.Contains("multidisorder")) badges.Add("multidisorder");
            if (methods.Contains("fakedsplit")) badges.Add("fakedsplit");
            if (methods.Contains("hostfakesplit")) badges.Add("hostfakesplit");
            if (methods.Contains("syndata")) badges.Add("syndata");
            if (methods.Contains("fake QUIC")) badges.Add("fake QUIC");
            if (methods.Contains("auto fake TLS")) badges.Add("auto fake TLS");
            else if (methods.Contains("fake TLS")) badges.Add("fake TLS");
            if (methods.Contains("Discord voice/STUN")) badges.Add("Discord voice");
            return badges.ToArray();
        }

        // ---- listing ----------------------------------------------------

        private static string NaturalKey(string name)
        {
            return Regex.Replace(name, @"(\d+)", m => m.Groups[1].Value.PadLeft(8, '0'));
        }

        public static List<StrategyInfo> ListStrategies(string rootDir)
        {
            var result = new List<StrategyInfo>();
            string[] files;
            try { files = Directory.GetFiles(rootDir ?? string.Empty, "*.bat"); }
            catch { return result; }

            foreach (var path in files)
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("service", StringComparison.OrdinalIgnoreCase)) continue;
                string title = Regex.Replace(name, @"\.bat$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, @"^\s*general[\s_-]*", "", RegexOptions.IgnoreCase);
                var badges = new string[0];
                try { badges = MethodBadges(ParseStrategyFile(path, rootDir).args); }
                catch { /* an unparsable file must still be listed */ }
                result.Add(new StrategyInfo
                {
                    file = path,
                    name = name,
                    title = string.IsNullOrEmpty(title) ? name : title,
                    badges = badges,
                    sortKey = NaturalKey(name),
                });
            }
            return result.OrderBy(s => s.sortKey, StringComparer.Ordinal).ToList();
        }

        // ---- import / delete --------------------------------------------

        public const string ImportPrefix = "user-";

        /// <summary>
        /// Clean a strategy file name for safe use inside the zapret folder.
        /// With prefix=true the name is tagged as user-supplied ("user-...");
        /// with prefix=false the original name is kept (sanitised only), which is
        /// what the import path uses so a custom "general (MY).bat" alt keeps its
        /// name and is picked up by the zapret tester's "All configs" sweep.
        /// </summary>
        public static string SafeStrategyName(string raw, bool prefix = true)
        {
            string baseName = Path.GetFileName(raw ?? string.Empty);
            baseName = Regex.Replace(baseName, @"\.bat$", "", RegexOptions.IgnoreCase);
            string clean = Regex.Replace(baseName, @"[^\w\s().-]+", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length == 0) return null;
            if (clean.Length > 80) clean = clean.Substring(0, 80);
            return (prefix ? ImportPrefix : "") + clean;
        }

        public sealed class OperationResult
        {
            public bool ok { get; set; }
            public string reason { get; set; }
            public string file { get; set; }
            public string name { get; set; }
            public string[] badges { get; set; } = new string[0];
            public string[] args { get; set; } = new string[0];
            public string message { get; set; }
        }

        public static OperationResult ImportStrategy(string rootDir, string srcFile)
        {
            string content;
            try { content = File.ReadAllText(srcFile); }
            catch { return new OperationResult { ok = false, reason = "read_failed" }; }
            if (!WinwsExe.IsMatch(content)) return new OperationResult { ok = false, reason = "no_winws" };
            // Keep the original name (no "user-" tag): a custom "general (MY).bat"
            // must look exactly like a built-in alt so the zapret tester's "All
            // configs" sweep picks it up. The File.Exists guard below still
            // refuses to overwrite an existing file.
            string name = SafeStrategyName(srcFile, prefix: false);
            if (name == null) return new OperationResult { ok = false, reason = "bad_name" };
            string dest = Path.Combine(rootDir, name + ".bat");
            if (File.Exists(dest)) return new OperationResult { ok = false, reason = "exists", file = dest };
            try { File.WriteAllText(dest, content); }
            catch { return new OperationResult { ok = false, reason = "write_failed" }; }
            var badges = new string[0];
            try { badges = MethodBadges(ParseStrategyFile(dest, rootDir).args); }
            catch { /* the imported file may be exotic */ }
            return new OperationResult { ok = true, file = dest, name = Path.GetFileName(dest), badges = badges };
        }

        public static OperationResult DeleteStrategy(string rootDir, string file)
        {
            string baseName = Path.GetFileName(file ?? string.Empty);
            if (!baseName.StartsWith(ImportPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new OperationResult { ok = false, reason = "protected" };
            }
            try
            {
                File.Delete(Path.Combine(rootDir, baseName));
                return new OperationResult { ok = true };
            }
            catch
            {
                return new OperationResult { ok = false, reason = "delete_failed" };
            }
        }

        public static bool IsImported(string name)
        {
            return (name ?? string.Empty).StartsWith(ImportPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}