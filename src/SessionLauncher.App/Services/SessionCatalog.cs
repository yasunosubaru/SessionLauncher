using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services
{
    /// <summary>
    /// Reads the conversation catalog out of a TOP-LEVEL-SESSIONS.md table.
    /// </summary>
    /// <remarks>
    /// The file is a GitHub-flavored markdown table whose columns are matched by
    /// NAME. Two layouts are in circulation:
    /// <c>| # | updated | msgs | agent | directory | title | session id |</c> and
    /// <c>| # | created | updated | msgs | agent | directory | title | session id |</c>.
    /// A catalog written before <c>created</c> existed still parses, with no creation
    /// time — see <see cref="RequiredColumns"/> for why that column is optional and
    /// the rest are not.
    /// <para>
    /// The <c>directory</c> and <c>session id</c> cells are wrapped in backticks, and
    /// a pipe inside a title is escaped as <c>\|</c> — so rows cannot be split naively.
    /// </para>
    /// </remarks>
    public sealed class SessionCatalog
    {
        /// <summary>Canonical file name, looked for in each candidate directory.</summary>
        public const string FileName = "TOP-LEVEL-SESSIONS.md";

        /// <summary>Column titles the table must carry, whatever order they appear in.</summary>
        private static readonly string[] RequiredColumns =
            { "#", "updated", "msgs", "agent", "directory", "title", "session id" };

        /// <summary>
        /// Where the session id sits in every layout this reader understands.
        /// </summary>
        /// <remarks>
        /// A row must be at least this wide to carry an id at all, and the id is
        /// the one field without which a row is useless — so this is the only
        /// index the parser ever needs in advance.
        /// </remarks>
        private const int IdColumn = 6;

        private readonly string _markdownPath;

        public SessionCatalog(string markdownPath)
        {
            _markdownPath = markdownPath ?? throw new ArgumentNullException(nameof(markdownPath));
        }

        /// <summary>Absolute path this catalog was constructed with.</summary>
        public string MarkdownPath => _markdownPath;

        /// <summary>Environment variable that overrides every candidate below.</summary>
        /// <remarks>
        /// This used to be a hardcoded absolute path on the machine the app was
        /// written on. That is the wrong shape for a published tool: it leaks the
        /// author's disk layout, and it silently fails for everyone else. The Node
        /// writer (<c>lib/catalog.mjs</c>) has always honoured this variable, so
        /// putting the reader on the same override also removes a real way for the
        /// two halves to disagree about which file they are talking about.
        /// </remarks>
        public const string CatalogEnvVar = "SESSIONLAUNCHER_CATALOG";

        /// <summary>
        /// Candidate catalog locations, in priority order. The first that exists wins.
        /// </summary>
        /// <remarks>
        /// Order matters more than it looks. This list MUST match
        /// <c>lib/catalog.mjs</c> exactly, or the generator writes one file and the
        /// GUI reads another.
        /// <para>
        /// The canonical per-user path leads the non-override entries on purpose. An
        /// earlier arrangement put the exe-local copy ahead of it, which is a trap:
        /// a stale <c>data\</c> beside the exe then outranks the live catalog and
        /// silently reports an old session list. That is precisely the drift the
        /// ordering exists to prevent, reached from the other direction.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> ProbeCandidatePaths(string exeDir)
        {
            var root = string.IsNullOrWhiteSpace(exeDir)
                ? AppContext.BaseDirectory
                : exeDir;

            var candidates = new List<string>();

            // An explicit override outranks everything: it is the user saying which
            // file they mean.
            var overridden = Environment.GetEnvironmentVariable(CatalogEnvVar);
            if (!string.IsNullOrWhiteSpace(overridden))
                candidates.Add(overridden);

            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SessionLauncher", FileName));
            candidates.Add(Path.Combine(root, "data", FileName));

            return candidates;
        }

        /// <summary>
        /// Resolve the first catalog that exists.
        /// </summary>
        /// <returns>The resolved path, or <c>null</c> when no candidate is present.</returns>
        public static string? ResolveCatalogPath(string exeDir)
            => ProbeCandidatePaths(exeDir).FirstOrDefault(File.Exists);

        /// <summary>
        /// Read the file, then parse it.
        /// </summary>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="InvalidDataException">
        /// The table is missing, or its header does not carry every required column.
        /// </exception>
        public IReadOnlyList<SessionInfo> Load()
        {
            if (!File.Exists(_markdownPath))
                throw new FileNotFoundException("Conversation catalog not found.", _markdownPath);

            string[] lines;
            using (var reader = new StreamReader(_markdownPath, Encoding.UTF8))
            {
                var all = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) is not null) all.Add(line);
                lines = all.ToArray();
            }

            return LoadFromLines(lines, _markdownPath);
        }

        /// <summary>
        /// Parse a catalog whose lines have already been read, with no file access.
        /// </summary>
        /// <param name="lines">The file's lines, in order.</param>
        /// <param name="sourceName">A name for error messages.</param>
        /// <exception cref="InvalidDataException">
        /// No header row is present, or no header row carries every required column.
        /// </exception>
        /// <remarks>
        /// Columns are resolved BY NAME, never by position. The catalog gained a
        /// <c>created</c> column, and every reader of this shared file — this one
        /// and the MCP server's — had the seven original fields hardcoded to
        /// indexes. A positional reader of a file it did not write is a reader that
        /// breaks the moment the file is regenerated, and it breaks the OTHER
        /// reader too, because they share one artifact.
        /// <para>
        /// <c>created</c> is deliberately not in <see cref="RequiredColumns"/>: a
        /// catalog generated before it existed must keep parsing, with no creation
        /// time rather than with no rows.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<SessionInfo> LoadFromLines(
            IReadOnlyList<string> lines, string sourceName)
        {
            ArgumentNullException.ThrowIfNull(lines);

            var headerIndex = -1;
            IReadOnlyDictionary<string, int>? map = null;
            for (var i = 0; i < lines.Count && map is null; i++)
            {
                if (TryBuildColumnMap(lines[i], out var candidate))
                {
                    map = candidate;
                    headerIndex = i;
                }
            }

            if (map is null)
                throw new InvalidDataException(
                    $"No usable catalog header row found in {sourceName}. Expected a row " +
                    $"carrying all of: {string.Join(", ", RequiredColumns)}.");

            // A name that is absent from a row is an empty cell, never an
            // IndexOutOfRange. A file caught mid-write must cost one field, not
            // the whole load.
            string Cell(string[] cells, string column)
                => map.TryGetValue(column, out var at) && at < cells.Length ? cells[at] : string.Empty;

            var sessions = new List<SessionInfo>();

            for (var i = headerIndex + 2; i < lines.Count; i++)   // +2 skips the |---|---| rule
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.TrimStart().StartsWith('|')) break;       // table ended

                var cells = SplitRow(line);
                if (cells.Length <= IdColumn) continue;

                var id = Clean(Cell(cells, "session id"));
                if (id.Length == 0) continue;

                var directory = Cell(cells, "directory");
                sessions.Add(new SessionInfo(
                    Id: id,
                    Title: Clean(Cell(cells, "title")),
                    Directory: Clean(directory),
                    Agent: Clean(Cell(cells, "agent")),
                    Updated: ParseTimestamp(Cell(cells, "updated")),
                    Messages: ParseMessages(Cell(cells, "msgs")),
                    ProjectPath: ToNativePath(Clean(directory)),
                    RawDirectory: directory.Trim())
                {
                    Created = ParseTimestamp(Cell(cells, "created")),
                });
            }

            if (sessions.Count == 0)
                throw new InvalidDataException(
                    $"Catalog {sourceName} has a valid header but no usable rows.");

            return sessions;
        }

        /// <summary>
        /// Try to read this line as the table header, returning a name-to-index map.
        /// </summary>
        /// <remarks>
        /// Succeeds for any header carrying every required column, in any order.
        /// That tolerance is the point; a strict positional header test would have
        /// rejected the very file this change was made to produce.
        /// </remarks>
        private static bool TryBuildColumnMap(string line, out IReadOnlyDictionary<string, int> map)
        {
            map = null!;
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('|')) return false;

            var cells = SplitRow(line).Select(Clean).Select(s => s.ToLowerInvariant()).ToArray();

            var candidate = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < cells.Length; i++)
                // First writer wins, so a duplicated column name cannot make a
                // later copy shadow the real one.
                if (cells[i].Length > 0 && !candidate.ContainsKey(cells[i]))
                    candidate[cells[i]] = i;

            foreach (var required in RequiredColumns)
                if (!candidate.ContainsKey(required)) return false;

            map = candidate;
            return true;
        }

        /// <summary>
        /// Split one markdown table row into cells, honouring <c>\|</c> escapes.
        /// The leading and trailing pipes are not cells.
        /// </summary>
        internal static string[] SplitRow(string line)
        {
            var text = line.Trim();
            if (text.StartsWith('|')) text = text[1..];
            if (text.EndsWith('|')) text = text[..^1];

            var cells = new List<string>();
            var current = new StringBuilder();

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    current.Append('|');   // consume the escape, keep the literal pipe
                    i++;
                    continue;
                }

                if (c == '|')
                {
                    cells.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            cells.Add(current.ToString());
            return cells.ToArray();
        }

        /// <summary>Strip backticks and surrounding whitespace from a cell.</summary>
        internal static string Clean(string cell)
        {
            var value = (cell ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '`' && value[^1] == '`')
                value = value[1..^1].Trim();
            return value;
        }

        internal static int ParseMessages(string cell)
            => int.TryParse(Clean(cell), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : 0;

        internal static DateTimeOffset ParseTimestamp(string cell)
            => DateTimeOffset.TryParse(
                Clean(cell), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var value)
                ? value
                : DateTimeOffset.MinValue;

        /// <summary>
        /// Convert a forward-slash directory from the catalog into a native path.
        /// Keeps the trailing separator so that drive roots stay openable in explorer.
        /// </summary>
        internal static string ToNativePath(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return string.Empty;

            var value = directory.Trim().Replace('/', '\\');

            // "C:" alone is not a folder; "C:\" is.
            if (value.Length == 2 && value[1] == ':') value += '\\';

            return value;
        }

        /// <summary>Number of assertions checked. Throws on the first failure.</summary>
        public static int RunSelfTest()
        {
            var n = 0;
            void Check(bool ok, string what)
            {
                n++;
                if (!ok) throw new InvalidOperationException("SessionCatalog self-test failed: " + what);
            }
            void CheckEqual<T>(T expected, T actual, string what)
            {
                n++;
                if (!EqualityComparer<T>.Default.Equals(expected, actual))
                    throw new InvalidOperationException(
                        $"SessionCatalog self-test failed: {what} (expected '{expected}', got '{actual}')");
            }
            void Throws(Action action, string what)
            {
                n++;
                try { action(); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException(
                    $"SessionCatalog self-test failed: {what} (nothing was thrown)");
            }

            // Timestamps parse with DateTimeStyles.AssumeLocal, so the offset
            // depends on this machine's zone. Comparing components rather than
            // whole values keeps the assertions true wherever they run.
            void At(DateTimeOffset value, int y, int mo, int d, int h, int mi, string what)
            {
                Check(value.Year == y && value.Month == mo && value.Day == d
                      && value.Hour == h && value.Minute == mi,
                      $"{what} (got {value:yyyy-MM-dd HH:mm})");
            }

            var seven = new[]
            {
                "# TOP-LEVEL-SESSIONS", "",
                "| # | updated | msgs | agent | directory | title | session id |",
                "| --- | --- | --- | --- | --- | --- | --- |",
                "| 1 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |",
                "| 2 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |",
            };

            var eight = new[]
            {
                "# TOP-LEVEL-SESSIONS", "",
                "| # | created | updated | msgs | agent | directory | title | session id |",
                "| --- | --- | --- | --- | --- | --- | --- | --- |",
                "| 1 | 2026-08-19 11:03 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |",
                "| 2 | 2026-07-02 08:00 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |",
            };

            // ---- the OLD seven-column layout still works ----
            //
            // This is the case that matters: the catalog is a shared file that the
            // generator rewrites, so the reader must survive every catalog the user
            // has not regenerated yet.
            var s7 = SessionCatalog.LoadFromLines(seven, "seven");
            CheckEqual(2, s7.Count, "the 7-column layout yields both rows");
            CheckEqual("title one | with pipe", s7[0].Title,
                "an escaped pipe inside a title resolves to a literal one");
            At(s7[0].Updated, 2026, 10, 1, 17, 39, "the 7-column updated cell parses");
            CheckEqual(DateTimeOffset.MinValue, s7[0].Created,
                "a layout with no created column yields MinValue, not a guess");
            CheckEqual(@"F:\a\b", s7[0].ProjectPath,
                "a forward-slash directory becomes a native path");
            CheckEqual("ses_two", s7[1].Id, "the second row's id is read");

            // ---- the NEW eight-column layout ----
            var s8 = SessionCatalog.LoadFromLines(eight, "eight");
            CheckEqual(2, s8.Count, "the 8-column layout yields both rows");
            CheckEqual("title one | with pipe", s8[0].Title,
                "the escaped pipe survives the extra column too");
            At(s8[0].Created, 2026, 8, 19, 11, 3, "the created cell parses");
            At(s8[1].Created, 2026, 7, 2, 8, 0, "each row gets its own created value");
            At(s8[0].Updated, 2026, 10, 1, 17, 39, "created did not displace updated");
            CheckEqual(1031, s8[0].Messages, "the message count still lands in the right cell");
            CheckEqual("build", s8[0].Agent, "the agent still lands in the right cell");
            CheckEqual(@"F:\a\b", s8[0].ProjectPath, "the native path is unaffected by the new column");

            // ---- order-independence, which is the whole point of name mapping ----
            var reversed = new[]
            {
                "| # | updated | msgs | agent | title | directory | session id |",
                "| --- | --- | --- | --- | --- | --- | --- |",
                "| 1 | 2026-10-01 17:39 | 5 | build | the title | `F:/a/b` | `ses_rev` |",
            };
            var rev = SessionCatalog.LoadFromLines(reversed, "reversed");
            CheckEqual("the title", rev[0].Title, "a reordered header does not swap title for directory");
            CheckEqual(@"F:\a\b", rev[0].ProjectPath, "a reordered header does not swap directory for title");

            // A layout where the id sits at index 1 rather than last. This is the shape a
            // name-based parser exists to support, and it also settles whether the
            // row-width guard is a guard or a bug: that guard counts how many CELLS
            // a row has, not where the id sits, so a 7-cell row clears it whatever
            // its column order.
            var idFirst = new[]
            {
                "| # | session id | updated | msgs | agent | title | directory |",
                "| --- | --- | --- | --- | --- | --- | --- |",
                "| 1 | `ses_early` | 2026-10-01 17:39 | 5 | build | the title | `F:/a/b` |",
            };
            var re = SessionCatalog.LoadFromLines(idFirst, "id-first");
            CheckEqual(1, re.Count, "a layout with the id in the second column parses");
            CheckEqual("ses_early", re[0].Id, "and the id is read from there");
            CheckEqual(@"F:\a\b", re[0].ProjectPath, "with the directory still correct");

            // ---- rejections ----
            Throws(() => SessionCatalog.LoadFromLines(
                    new[] { "# x", "", "| foo | bar |", "| --- | --- |", "| 1 | 2 |" }, "junk"),
                "a table whose header is neither layout is rejected");
            Throws(() => SessionCatalog.LoadFromLines(
                    new[] { "| # | updated | msgs | agent | directory | session id |" }, "no-title"),
                "a header missing the title column is rejected");
            Throws(() => SessionCatalog.LoadFromLines(new[] { "# nothing here" }, "empty"),
                "a file with no table at all is rejected");

            // ---- an unusable created cell is "no value", not a crash ----
            var blank = new[]
            {
                "| # | created | updated | msgs | agent | directory | title | session id |",
                "| --- | --- | --- | --- | --- | --- | --- | --- |",
                "| 1 |  | 2026-10-01 17:39 | 1 | build | `F:/a` | t | `ses_blank` |",
                "| 2 | not-a-date | 2026-10-01 17:39 | 1 | build | `F:/b` | t | `ses_junk` |",
            };
            var b = SessionCatalog.LoadFromLines(blank, "blank");
            CheckEqual(2, b.Count, "an empty or unparseable created cell does not drop the row");
            CheckEqual(DateTimeOffset.MinValue, b[0].Created, "an empty created cell is MinValue");
            CheckEqual(DateTimeOffset.MinValue, b[1].Created, "an unparseable created cell is MinValue");

            // ---- a truncated row keeps whatever it has ----
            var shortRow = new[]
            {
                "| # | created | updated | msgs | agent | directory | title | session id |",
                "| --- | --- | --- | --- | --- | --- | --- | --- |",
                "| 1 | 2026-08-19 11:03 | 2026-10-01 17:39 | 1031 | build |",
                "| 2 | 2026-07-02 08:00 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |",
            };
            var sr = SessionCatalog.LoadFromLines(shortRow, "short");
            CheckEqual(1, sr.Count, "a row that lost its cells is skipped, and the rest still load");
            CheckEqual("ses_two", sr[0].Id, "the intact row is the one that survives");

            return n;
        }
    }
}
