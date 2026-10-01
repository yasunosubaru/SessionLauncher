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
    /// The file is a GitHub-flavored markdown table with a fixed 8-column header:
    /// <c>| # | updated | msgs | agent | directory | title | session id |</c>.
    /// The <c>directory</c> and <c>session id</c> cells are wrapped in backticks, and
    /// a pipe inside a title is escaped as <c>\|</c> - so rows cannot be split naively.
    /// </remarks>
    public sealed class SessionCatalog
    {
        /// <summary>Canonical file name, looked for in each candidate directory.</summary>
        public const string FileName = "TOP-LEVEL-SESSIONS.md";

        /// <summary>Column titles that identify the header row, in order.</summary>
        private static readonly string[] ExpectedHeader =
            { "#", "updated", "msgs", "agent", "directory", "title", "session id" };

        private readonly string _markdownPath;

        public SessionCatalog(string markdownPath)
        {
            _markdownPath = markdownPath ?? throw new ArgumentNullException(nameof(markdownPath));
        }

        /// <summary>Absolute path this catalog was constructed with.</summary>
        public string MarkdownPath => _markdownPath;

        /// <summary>Canonical catalog location, shared with the Node MCP server.</summary>
        private const string CanonicalCatalogPath = @"<catalog dir>\TOP-LEVEL-SESSIONS.md";

        /// <summary>
        /// Candidate catalog locations, in priority order. The first that exists wins.
        /// </summary>
        /// <remarks>
        /// The canonical path leads on purpose. The exe-local copy is only a portable
        /// fallback: if the MCP writer targeted it while this reader preferred the
        /// canonical file, the two would drift and report different session counts.
        /// The Node server (<c>lib/catalog.mjs</c>) uses the same order.
        /// </remarks>
        public static IReadOnlyList<string> ProbeCandidatePaths(string exeDir)
        {
            var root = string.IsNullOrWhiteSpace(exeDir)
                ? AppContext.BaseDirectory
                : exeDir;

            return new[]
            {
                CanonicalCatalogPath,
                Path.Combine(root, "data", FileName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SessionLauncher", FileName),
                @"<repo>\apps\SessionLauncher\data\" + FileName,
            };
        }

        /// <summary>
        /// Resolve the first catalog that exists.
        /// </summary>
        /// <returns>The resolved path, or <c>null</c> when no candidate is present.</returns>
        public static string? ResolveCatalogPath(string exeDir)
            => ProbeCandidatePaths(exeDir).FirstOrDefault(File.Exists);

        /// <summary>
        /// Parse the catalog file.
        /// </summary>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="InvalidDataException">
        /// The table is missing, or its header is not the expected 8 columns.
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

            var headerIndex = Array.FindIndex(lines, IsHeaderRow);
            if (headerIndex < 0)
                throw new InvalidDataException(
                    $"No catalog header row found in {_markdownPath}. Expected a row whose " +
                    $"cells are: {string.Join(" | ", ExpectedHeader)}.");

            var sessions = new List<SessionInfo>();
            var skipped = 0;

            for (var i = headerIndex + 2; i < lines.Length; i++)   // +2 skips the |---|---| rule
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.TrimStart().StartsWith('|')) break;       // table ended

                var cells = SplitRow(line);
                if (cells.Length < 7)
                {
                    skipped++;
                    continue;
                }

                var id = Clean(cells[6]);
                if (id.Length == 0) { skipped++; continue; }

                sessions.Add(new SessionInfo(
                    Id: id,
                    Title: Clean(cells[5]),
                    Directory: Clean(cells[4]),
                    Agent: Clean(cells[3]),
                    Updated: ParseTimestamp(cells[1]),
                    Messages: ParseMessages(cells[2]),
                    ProjectPath: ToNativePath(Clean(cells[4])),
                    RawDirectory: cells[4].Trim()));
            }

            if (sessions.Count == 0)
                throw new InvalidDataException(
                    $"Catalog {_markdownPath} has a valid header but no usable rows " +
                    $"({skipped} row(s) skipped).");

            return sessions;
        }

        /// <summary>True when this line is the table header.</summary>
        private static bool IsHeaderRow(string line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('|')) return false;

            var cells = SplitRow(line).Select(Clean).Select(s => s.ToLowerInvariant()).ToArray();
            if (cells.Length < ExpectedHeader.Length) return false;

            for (var i = 0; i < ExpectedHeader.Length; i++)
                if (cells[i] != ExpectedHeader[i]) return false;

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
    }
}
