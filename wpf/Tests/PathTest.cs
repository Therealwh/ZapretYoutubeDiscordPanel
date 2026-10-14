// Path-independence test: parse, launch and (optionally) install a strategy
// from a zapret root whose path contains SPACES and lives on another drive.
//
//   YoutubeDiscordPanel.exe --selftest --root "D:\Program Files\AZ Tools\zapret"
//
// This exists because the whole issue #3 complaint was "it only works from
// C:\Zapret" - so the claim has to be verified against a real spaced path, not
// argued about.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ydp.Services;
using Ydp.Zapret;

namespace Ydp.Tests
{
    public static class PathTest
    {
        public static int Run(string root)
        {
            int failures = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name + (detail == null ? "" : " — " + detail));
                if (!ok) failures++;
            };

            Console.WriteLine("path test root: " + root);
            check("root exists", Directory.Exists(root), root);

            var validation = Status.ValidateRoot(root);
            check("validateRoot ok", validation.ok, validation.reason);
            check("no 'spaces' complaint for a spaced path", !validation.issues.Contains("spaces"),
                string.Join(",", validation.issues));

            var strategies = Strategy.ListStrategies(root);
            check("strategies listed", strategies.Count > 0, "найдено " + strategies.Count);
            // A wrong or truncated --root value must be a clean failure, not an
            // index-out-of-range crash on strategies[0].
            if (strategies.Count == 0)
            {
                Console.WriteLine("PATHTEST FAIL нет стратегий — проверьте путь (он мог быть обрезан пробелом)");
                return failures + 1;
            }

            var withBadges = strategies.FirstOrDefault(s => s.badges.Length > 0);
            check("badges detected from a spaced path", withBadges != null,
                withBadges == null ? "все без бейджей" : withBadges.name);

            var parsed = Strategy.ParseStrategyFile(strategies[0].file, root);
            check("strategy parsed", parsed.args.Count > 0, "����������: " + parsed.args.Count);

            // Every path argument must be absolute AND quoted so winws'
            // own tokenizer keeps "D:\Program Files\..." in one piece.
            var pathArgs = parsed.args.Where(a => a.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            check("path arguments reference the spaced root", pathArgs.Count > 0, string.Join(" | ", pathArgs.Take(3)));
            check("path arguments are quoted", pathArgs.All(a => a.StartsWith("\"") && a.EndsWith("\"")),
                string.Join(" | ", pathArgs.Take(3)));

            var rejoined = Strategy.RejoinFlagValues(parsed.args);
            check("flag values rejoined for direct spawn", rejoined.Any(a => a.StartsWith("--hostlist=")),
                rejoined.FirstOrDefault(a => a.StartsWith("--hostlist=")) ?? "нет --hostlist");

            Lists.EnsureUserLists(root);
            var userList = Path.Combine(root, "lists", "list-general-user.txt");
            check("user lists created in the spaced root", File.Exists(userList), userList);

            // The real test: start winws from this path and see it appear.
            var launch = ServiceOps.LaunchStrategyProcess(root, strategies[0].file);
            check("winws launched from a spaced path", launch.ok, launch.reason + " " + launch.message);
            ServiceOps.KillWinws();
            ServiceOps.WaitWinws(true, 8000);

            if (Win.IsAdmin())
            {
                var parsedForPath = Strategy.ParseStrategyFile(strategies[0].file, root);
                var imagePath = ServiceOps.BuildImagePath(
                    System.IO.Path.Combine(root, "bin", "winws.exe"),
                    Strategy.RejoinFlagValues(parsedForPath.args));
                Console.WriteLine("  imagePath: " + imagePath);

                var install = ServiceOps.InstallStrategyAsService(root, strategies[0].file);
                check("service installed from a spaced path", install.ok, install.reason + " " + install.message);
                var state = Win.QueryService("zapret");
                check("service reports RUNNING", state.Running, state.State);
                ServiceOps.RemoveServices();
                var after = Win.QueryService("zapret");
                check("service removed", !after.Exists, after.State);
            }
            else
            {
                Console.WriteLine("  skip service part: no admin rights");
            }

            Console.WriteLine(failures == 0 ? "PATHTEST OK" : "PATHTEST FAIL " + failures);
            return failures;
        }
    }
}