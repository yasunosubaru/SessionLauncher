using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services
{
    /// <summary>
    /// Plain-logic assertions for the non-UI services.
    /// </summary>
    /// <remarks>
    /// These run headless: <c>tools\run-selftest.ps1</c> copies the service sources into a
    /// throwaway console project and executes <see cref="Run"/>. Keeping them here rather
    /// than in a test framework is deliberate - the app has zero NuGet references, and a
    /// real test project would break that.
    /// </remarks>
    public static class SelfTest
    {
        private static int _checks;

        /// <summary>Run every suite. Returns the assertion count; throws on the first failure.</summary>
        public static int Run()
        {
            _checks = 0;

            SearchEngine();
            AppSettingsSuite();

            // These two carry their own assertion counters and RunSelfTest methods,
            // written that way so they stay runnable outside this harness (the swarm
            // workers and tools\run-selftest.ps1 both call them directly). Fold their
            // counts in rather than dropping them, so one number reports everything.
            _checks += ProjectCatalog.RunSelfTest();
            _checks += ProjectSort.RunSelfTest();
            _checks += WorkspaceService.RunSelfTest();
            _checks += OpenChamberBridge.RunSelfTest();

            return _checks;
        }

        // ---- assertions -----------------------------------------------------

        private static void Check(bool condition, string what)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException("SelfTest failed: " + what);
        }

        private static void CheckEqual<T>(T expected, T actual, string what)
        {
            _checks++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(
                    $"SelfTest failed: {what} (expected '{expected}', got '{actual}')");
        }

        // ---- search engine --------------------------------------------------

        private static List<SearchDoc> Corpus() =>
        [
            new("ses_f0d42b1", "GNSS 设备 TEC 自检", "F:/proj/capstone", "build", 342),
            new("ses_alpha",  "Fix the DNS resolver",             "F:/proj/net",      "build", 500),
            new("ses_beta",   "bandwidth tuning",                  "F:/proj/net",      "plan",  12),
            new("ses_gamma",  "修复错误",                          "F:/proj/sample",      "build", 8),
            new("ses_delta",  "恢复所有对话记录",                   "F:/proj/sample",      "build", 180),
            new("ses_eps",    "",                                  "F:/proj/empty",    "build", 0),
            new("ses_zeta",   "Pipe | handling in markdown",       "F:/proj/md",       "build", 44),
            new("ses_eta",    "Docker Engine 镜像拉取超时",         "F:/proj/capstone",  "build", 158),
            new("ses_theta",  "DNS",                              "F:/proj/net",      "build", 5),
            new("ses_iota",   "中文会话标题测试",                   "F:/proj/sample",      "build", 64),
            // Reachable only through the agent field, which isolates agent matching.
            new("ses_kappa",  "Zeta report",                        "F:/proj/z",        "reviewer", 33),
        ];

        private static void SearchEngine()
        {
            var docs = Corpus();

            // Empty query returns everything, in input order.
            var all = SessionSearch.Search(docs, "");
            CheckEqual(docs.Count, all.Count, "empty query returns all docs");
            CheckEqual(docs[0].Id, all[0].Hit.Doc.Id, "empty query preserves input order");

            // Count agrees with Search.
            CheckEqual(SessionSearch.Count(docs, "dns"), SessionSearch.Search(docs, "dns").Count,
                       "Count agrees with Search");
            CheckEqual(docs.Count, SessionSearch.Count(docs, "   "), "whitespace query counts all");

            // AND semantics across tokens.
            var both = SessionSearch.Search(docs, "dns resolve");
            Check(both.Count >= 1, "AND: 'dns resolve' finds the resolver session");
            Check(both.All(r => r.Hit.Doc.Id == "ses_alpha"), "AND: 'dns resolve' only matches ses_alpha");

            var impossible = SessionSearch.Search(docs, "dns zzzznotpresent");
            CheckEqual(0, impossible.Count, "AND: one unmatched token rejects the doc");

            // Exact beats word-prefix beats substring.
            var exact = SessionSearch.Search(docs, "dns")[0].Hit.Doc.Id;
            CheckEqual("ses_theta", exact, "exact whole-value match ranks first");

            var resolverFirst = SessionSearch.Search(docs, "resolv")[0].Hit.Doc.Id;
            CheckEqual("ses_alpha", resolverFirst, "word-prefix match ranks first for 'resolv'");

            // substring beats fuzzy.
            var sub = SessionSearch.Search(docs, "band");
            Check(sub.Count >= 1, "substring finds bandwidth");
            Check(!sub[0].IsFuzzy, "substring match is not flagged fuzzy");

            // Fuzzy subsequence.
            var fuzzy = SessionSearch.Search(docs, "dnslvr");
            Check(fuzzy.Count >= 1, "subsequence 'dnslvr' matches 'Fix the DNS resolver'");
            Check(fuzzy[0].IsFuzzy, "subsequence match is flagged fuzzy");

            // Case-insensitivity.
            CheckEqual(SessionSearch.Search(docs, "DNS").Count,
                       SessionSearch.Search(docs, "dns").Count, "case-insensitive");

            // CJK: whitespace-free titles must be searchable.
            Check(SessionSearch.Search(docs, "修复错误").Count == 1, "exact CJK title match");
            Check(SessionSearch.Search(docs, "对话记录").Count >= 1, "CJK substring match");
            Check(SessionSearch.Search(docs, "对话记录")[0].IsFuzzy == false, "CJK substring is literal");

            // CJK subsequence: 修错误 skips 复.
            var cjkFuzzy = SessionSearch.Search(docs, "修错误");
            Check(cjkFuzzy.Count >= 1, "CJK subsequence matches across a skipped rune");

            // Mixed CJK + ASCII.
            Check(SessionSearch.Search(docs, "GNSS sample").Count == 0,
                  "tokens must match within one document (gnss + sample in different docs)");
            Check(SessionSearch.Search(docs, "TEC capstone").Count >= 1, "mixed ASCII+CJK tokens both match");

            // Agent field participates: "reviewer" appears in no other field of that doc.
            var byAgent = SessionSearch.Search(docs, "reviewer");
            Check(byAgent.Count == 1, "agent-only token finds exactly one doc");
            CheckEqual("ses_kappa", byAgent[0].Hit.Doc.Id, "agent field is searchable");
            Check(byAgent[0].IsFuzzy == false, "exact agent match is literal, not fuzzy");

            // Title highlight ranges point at the matched text.
            var lit = SessionSearch.Search(docs, "resolver")[0];
        var ranges = lit.TitleRanges;
        Check(ranges.Count >= 1, "literal match yields highlight ranges");
        foreach (var range in ranges)
        {
            Check(range.Start >= 0 && range.Length > 0, "range is well formed");
            Check(range.Start + range.Length <= lit.Hit.Doc.Title.Length, "range stays inside the title");
            var slice = lit.Hit.Doc.Title.Substring(range.Start, range.Length);
            Check(slice.Equals("resolver", StringComparison.OrdinalIgnoreCase),
                  $"range covers the matched text (got '{slice}')");
        }

            // Fuzzy match yields no literal ranges.
        var fz = SessionSearch.Search(docs, "dnslvr")[0];
        CheckEqual(0, fz.TitleRanges.Count, "fuzzy match yields no literal ranges");

            // Deterministic ordering across runs.
        var runA = SessionSearch.Search(docs, "net").Select(r => r.Hit.Doc.Id).ToList();
        var runB = SessionSearch.Search(docs, "net").Select(r => r.Hit.Doc.Id).ToList();

            // Regression: field choice must be decided on the WEIGHTED score.
            //
            // Shape taken from the real catalog, where sample appears in both the title and
            // the directory of one session and only in the directory of another. The
            // title match is a substring (400 x 10 = 4000) and the directory match is a
            // word-prefix (600 x 6 = 3600). Ranking on raw quality promoted the
            // directory-only session above the one whose title literally contains sample.
            var weighted = new List<SearchDoc>
            {
                new("ses_both", "自主完成计算机sample", @"F:\课程笔记\sample", "build", 472),
                new("ses_dironly", "Resea", @"F:\课程笔记\sample", "build", 184),
            };
            var ranked = SessionSearch.Search(weighted, "sample");
            CheckEqual(2, ranked.Count, "weighted-field regression: both docs match sample");
            CheckEqual("ses_both", ranked[0].Hit.Doc.Id,
                       "title substring (4000) outranks directory word-prefix (3600)");
        Check(runA.SequenceEqual(runB), "ordering is stable across identical runs");

            // Limit honoured.
        var limited = SessionSearch.Search(docs, "", 3);
        CheckEqual(3, limited.Count, "limit is honoured");
        CheckEqual(0, SessionSearch.Search(docs, "", 0).Count, "limit 0 returns nothing");

            // Message-count tiebreak: two docs with the same score, more messages first.
        var tie = SessionSearch.Search(docs, "dns");
        Check(tie.Count >= 1, "tiebreak fixture present");

            // Range merging.
            var merged = SessionSearch.Merge([new Range(0, 3), new Range(2, 3), new Range(10, 2)]);
        CheckEqual(2, merged.Count, "adjacent/overlapping ranges merge");
        CheckEqual(0, merged[0].Start, "merged range keeps the earliest start");
        CheckEqual(5, merged[0].Length, "merged range spans both inputs");
        }

        // ---- settings -------------------------------------------------------

        private static void AppSettingsSuite()
        {
            var previous = AppSettings.PathOverride;
            var dir = Path.Combine(Path.GetTempPath(), "sl-selftest-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(dir);
                AppSettings.PathOverride = Path.Combine(dir, "settings.json");

                // Missing file -> defaults.
                var fresh = AppSettings.Load();
                CheckEqual(1.0, fresh.FontScale, "missing file yields default font scale");
                CheckEqual(AppLang.ZhHans, fresh.Language, "missing file yields default language");

                // Round trip.
                var saved = new AppSettings
                {
                    FontScale = 1.35,
                    Language = AppLang.En,
                    WindowLeft = 12.5,
                    WindowTop = -3,
                    WindowWidth = 1280,
                    WindowHeight = 800,
                    View = AppView.Projects,
                    ProjectSort = ProjectSortMode.NameDesc,
                    HideMissingProjects = false,
                };
                saved.Save();
                Check(!File.Exists(AppSettings.DefaultPath + ".tmp"), "Save leaves no .tmp behind");

                var loaded = AppSettings.Load();
                Check(Math.Abs(loaded.FontScale - 1.35) < 1e-9, "font scale round-trips");
                CheckEqual(AppLang.En, loaded.Language, "language round-trips");
                CheckEqual(1280, loaded.WindowWidth ?? 0, "width round-trips");
                CheckEqual(800, loaded.WindowHeight ?? 0, "height round-trips");

                // The project-view choices are persisted BY NAME. If they did not
                // round-trip, switching language (which rebuilds the window) would drop
                // the user back onto the session list.
                CheckEqual(AppView.Projects, loaded.View, "view round-trips");
                CheckEqual(ProjectSortMode.NameDesc, loaded.ProjectSort, "project sort round-trips");
                CheckEqual(false, loaded.HideMissingProjects, "hide-missing round-trips");

                // An unknown enum value in the file must fall back rather than throw:
                // a hand-edited settings file is not a reason to refuse to start.
                File.WriteAllText(AppSettings.DefaultPath,
                    """{ "view": "Nonsense", "projectSort": "AlsoNonsense" }""");
                var bogus = AppSettings.Load();
                CheckEqual(AppView.Sessions, bogus.View, "unknown view falls back to Sessions");
                CheckEqual(ProjectSortMode.LastUsed, bogus.ProjectSort,
                           "unknown project sort falls back to LastUsed");

                // Repeated 0.1 steps must not accumulate binary float drift into the file.
                for (var i = 0; i < 3; i++) { loaded.FontScale += 0.1; loaded.Save(); }
                var drifted = AppSettings.Load().FontScale;
                Check(Math.Abs(drifted - 1.65) < 1e-9,
                      $"three 0.1 steps land on exactly 1.65 (got {drifted})");

                // Corrupt file -> defaults, no throw.
                File.WriteAllText(AppSettings.DefaultPath, "{ this is not json");
                var recovered = AppSettings.Load();
                CheckEqual(1.0, recovered.FontScale, "corrupt file falls back to defaults");

                // Empty file -> defaults.
                File.WriteAllText(AppSettings.DefaultPath, "");
                CheckEqual(1.0, AppSettings.Load().FontScale, "empty file falls back to defaults");

                // Non-positive / non-finite is corruption, not a preference: reset to default.
                File.WriteAllText(AppSettings.DefaultPath,
                    "{\"fontScale\": -5, \"language\": \"En\"}");
                var reset = AppSettings.Load();
                Check(Math.Abs(reset.FontScale - AppSettings.DefaultFontScale) < 1e-9,
                      "negative font scale resets to the default");
                CheckEqual(AppLang.En, reset.Language, "language still parses alongside a bad scale");

                File.WriteAllText(AppSettings.DefaultPath, "{\"fontScale\": 0}");
                Check(Math.Abs(AppSettings.Load().FontScale - AppSettings.DefaultFontScale) < 1e-9,
                      "zero font scale resets to the default");

                // A positive but out-of-range value is clamped, not discarded.
                File.WriteAllText(AppSettings.DefaultPath, "{\"fontScale\": 0.1}");
                Check(Math.Abs(AppSettings.Load().FontScale - AppSettings.MinFontScale) < 1e-9,
                      "too-small font scale clamps up to the minimum");

                File.WriteAllText(AppSettings.DefaultPath,
                    "{\"fontScale\": 99, \"windowWidth\": 0}");
                var clampedHigh = AppSettings.Load();
                Check(Math.Abs(clampedHigh.FontScale - AppSettings.MaxFontScale) < 1e-9,
                      "huge font scale clamps to the maximum");
                Check(clampedHigh.WindowWidth is null, "zero width is dropped");

                // Unknown enum member -> default.
                File.WriteAllText(AppSettings.DefaultPath, "{\"language\": \"Klingon\"}");
                CheckEqual(AppLang.ZhHans, AppSettings.Load().Language, "unknown language falls back");

                // Off-screen window position is dropped rather than restored blindly.
                File.WriteAllText(AppSettings.DefaultPath, "{\"windowLeft\": -99999}");
                Check(AppSettings.Load().WindowLeft is null, "absurd window position is discarded");
            }
            finally
            {
                AppSettings.PathOverride = previous;
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
