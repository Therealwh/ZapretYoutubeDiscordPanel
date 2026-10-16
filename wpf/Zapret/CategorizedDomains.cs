using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ydp.Zapret
{
    /// <summary>
    /// Organises the "general" domain list into named sections:
    ///
    ///   # === Discord ===
    ///   discord.com
    ///   ...
    ///   # === YouTube ===
    ///   ...
    ///   # === Telegram ===
    ///   kws1....
    ///   # === Другое ===      ← the user's own entries, untouched
    ///
    /// Section headers are ordinary "#" comments, which the zapret list parser
    /// and the panel already ignore — so this is purely about making the file
    /// readable. Every operation MERGES: it only ever adds the domains it
    /// manages and never rewrites or removes anything else, which is what made
    /// the earlier "preset overwrote the Telegram domains" bug possible.
    /// </summary>
    public static class CategorizedDomains
    {
        public const string HeaderOpen = "# === ";
        public const string HeaderClose = " ===";
        public const string SectionOther = "Другое";
        public const string SectionDiscord = "Discord";
        public const string SectionYouTube = "YouTube";
        public const string SectionTelegram = "Telegram";

        public static readonly string[] AllSections =
            { SectionDiscord, SectionYouTube, SectionTelegram, SectionOther };

        private static string Header(string category) => HeaderOpen + category + HeaderClose;

        private static bool IsHeader(string line, string category)
        {
            return string.Equals(line.Trim(), Header(category), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnyHeader(string line)
        {
            var t = line.Trim();
            return t.StartsWith(HeaderOpen, StringComparison.Ordinal)
                && t.EndsWith(HeaderClose, StringComparison.Ordinal);
        }

        private static bool IsDomainLine(string line)
        {
            var t = line.Trim();
            return t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal);
        }

        /// <summary>
        /// Ensure a section for <paramref name="category"/> exists and contains all
        /// <paramref name="domains"/>. Everything else in the file is preserved
        /// byte-for-byte. Creates the section (at the end) if it is missing.
        /// </summary>
        public static string Ensure(string text, string category, IEnumerable<string> domains)
        {
            var lines = SplitLines(text);
            int header = -1;
            for (int i = 0; i < lines.Count; i++)
                if (IsHeader(lines[i], category)) { header = i; break; }

            var wanted = (domains ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .ToList();

            if (header < 0)
            {
                // No section yet: create one at the end.
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add(string.Empty);
                lines.Add(Header(category));
                foreach (var d in wanted)
                    if (!ContainsDomain(lines, d)) lines.Add(d);
                return JoinLines(lines);
            }

            // Section exists: find where it ends (next header or EOF) and add
            // the missing domains just before that boundary.
            int end = lines.Count;
            for (int i = header + 1; i < lines.Count; i++)
                if (IsAnyHeader(lines[i])) { end = i; break; }

            int insertAt = end;
            while (insertAt > header + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;

            foreach (var d in wanted)
            {
                if (ContainsDomain(lines, d)) continue;
                lines.Insert(insertAt++, d);
            }
            return JoinLines(lines);
        }

        /// <summary>
        /// Make sure every managed section header exists (empty ones are fine),
        /// so the file is laid out even before anything is filled in. Does not
        /// touch section contents.
        /// </summary>
        public static string EnsureAllSections(string text)
        {
            var result = text ?? string.Empty;
            foreach (var section in AllSections)
                result = Ensure(result, section, null);
            return result;
        }

        /// <summary>
        /// Rebuild the whole file as the four sections, moving every domain line
        /// into the right one: a domain that belongs to a managed set goes to
        /// that section, anything else lands in "Другое". Duplicates are dropped.
        /// Standalone comment lines (that are not section headers) are kept at
        /// the top. This is what turns a previously flat list into the categorised
        /// layout without losing a single domain.
        /// </summary>
        public static string Organize(string text,
            IEnumerable<string> discord, IEnumerable<string> youtube, IEnumerable<string> telegram)
        {
            var lines = SplitLines(text);
            var keepComments = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byCategory = new Dictionary<string, List<string>>();
            foreach (var section in AllSections) byCategory[section] = new List<string>();

            foreach (var line in lines)
            {
                if (IsAnyHeader(line)) continue;             // old layout, rebuilt below
                if (!IsDomainLine(line))
                {
                    if (line.Trim().Length > 0 && line.Trim().StartsWith("#", StringComparison.Ordinal))
                        keepComments.Add(line.Trim());
                    continue;
                }
                var domain = line.Trim();
                if (!seen.Add(domain)) continue;              // de-duplicate
                byCategory[Classify(domain, discord, youtube, telegram)].Add(domain);
            }

            var outp = new List<string>(keepComments);
            foreach (var section in AllSections)
            {
                if (outp.Count > 0) outp.Add(string.Empty);
                outp.Add(Header(section));
                foreach (var d in byCategory[section]) outp.Add(d);
            }
            return JoinLines(outp);
        }

        private static string Classify(string domain,
            IEnumerable<string> discord, IEnumerable<string> youtube, IEnumerable<string> telegram)
        {
            if (Matches(discord, domain)) return SectionDiscord;
            if (Matches(youtube, domain)) return SectionYouTube;
            if (Matches(telegram, domain)) return SectionTelegram;
            return SectionOther;
        }

        private static bool Matches(IEnumerable<string> set, string domain)
        {
            if (set == null) return false;
            foreach (var s in set)
                if (string.Equals(s, domain, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ContainsDomain(List<string> lines, string domain)
        {
            return lines.Any(l => IsDomainLine(l)
                && string.Equals(l.Trim(), domain, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            list.AddRange(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            return list;
        }

        private static string JoinLines(List<string> lines)
        {
            return string.Join(Environment.NewLine, lines);
        }
    }
}
