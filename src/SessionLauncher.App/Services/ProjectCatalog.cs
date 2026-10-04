// ProjectCatalog.cs
//
// The 项目 / 工作区 / 会话 model the user actually described:
//
//     项目 (project)  ─ a directory
//        └─ 工作区 (workspace) ─ an OpenChamber/OpenCode worktree
//             └─ 会话 (session)  ─ the smallest unit
//
// A project is therefore "a set of sessions that share a directory", which is
// exactly what the catalog gives us once the sessions are grouped. This file does
// the grouping and assigns each project a colour.
//
// WHY THE COLOURS ARE SPECIFIC HEXES
// OpenChamber's PROJECT_COLORS (packages/ui/src/lib/projectMeta.ts) does not name
// colours, it points at CSS variables:
//
//   { key: 'keyword',  cssVar: 'var(--syntax-keyword)' }   -> label 'Purple'
//   { key: 'comment',  cssVar: 'var(--syntax-comment)' }   -> label 'Muted'
//   { key: 'error',    cssVar: 'var(--status-error)' }     -> label 'Red'
//   ...
//
// The labels do not match the actual values, which is a trap: 'keyword' is
// labelled Purple but --syntax-keyword resolves to #34983a, a green. The values
// below are read from the default dark theme the screenshots were taken with,
// openchamber-dark.json:
//
//   --syntax-keyword  #34983a      --status-error     #da5b4a
//   --syntax-string   #d58373      --status-success   #76ad4f
//   --syntax-number   #279e93      --primary.base     #da7c47
//   --syntax-type     #479cb1      --syntax-comment   #728772
//   --syntax-variable #c69457      (this is what 'constant' points at)
//
// So a project rendered in SessionLauncher matches one in OpenChamber instead of
// merely being 'some colour'. The order is OpenChamber's own PROJECT_COLORS order,
// because pickColor below reproduces its least-used-first algorithm and would drift
// if the order differed.

using System;
using System.Collections.Generic;
using System.Linq;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services;

/// <summary>A colour OpenChamber can assign to a project.</summary>
public sealed record ProjectColor(string Key, string Label, string Hex);

/// <summary>One project: a directory plus every session that lives in it.</summary>
public sealed class ProjectEntry
{
    public required string Path { get; init; }

    /// <summary>
    /// Display name. The leaf folder name, except where a grouping rule supplies a
    /// friendlier one.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>OpenChamber's colour key for this project, or null when it has none.</summary>
    public string? ColorKey { get; init; }

    /// <summary>Resolved colour for <see cref="ColorKey"/>.</summary>
    public string ColorHex { get; init; } = ProjectCatalog.DefaultColorHex;

    /// <summary>Every session in this project, most recently updated first.</summary>
    public required IReadOnlyList<SessionInfo> Sessions { get; init; }

    public int SessionCount => Sessions.Count;

    /// <summary>Newest session in the project.</summary>
    public DateTimeOffset LastUsed => Sessions.Count > 0 ? Sessions[0].Updated : DateTimeOffset.MinValue;

    /// <summary>Oldest session in the project.</summary>
    public DateTimeOffset FirstUsed =>
        Sessions.Count > 0 ? Sessions[^1].Updated : DateTimeOffset.MinValue;

    /// <summary>Total messages across the project's sessions, a workload proxy.</summary>
    public int MessageCount => Sessions.Sum(s => s.Messages);

    /// <summary>
    /// When this project came into existence, i.e. the earliest creation time among
    /// its sessions.
    /// </summary>
    /// <remarks>
    /// A real <c>Min</c>, not <c>Sessions[^1].Created</c>. <see cref="Sessions"/> is
    /// ordered by <see cref="LastUsed"/>, so its last element is the least recently
    /// used session — which is not necessarily the oldest one. A conversation edited
    /// today after being created last year would make the project's creation date
    /// report as today.
    /// <para>
    /// Computed rather than stored: the session list is the only source, and it is
    /// replaced wholesale every time the catalog reloads.
    /// <para>
    /// Sessions with no creation time are EXCLUDED rather than counted as the
    /// earliest. A catalog can mix dated and undated rows — a NULL time_created, or a
    /// hand-edit — and treating the gaps as the year one would make a single missing
    /// value report the whole project as having no creation date, discarding forty real
    /// dates because of one gap.
    /// </para>
    /// </remarks>
    public DateTimeOffset Created =>
        Sessions.Where(s => s.Created != DateTimeOffset.MinValue)
                .Select(s => s.Created)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Min();

    /// <summary>Distinct agents that have worked in this project.</summary>
    public int AgentCount =>
        Sessions.Select(s => s.Agent).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Count();

    // Fully qualified: this class has its own Path property, which shadows the
    // System.IO.Path type, and the same shadowing hazard applies to Directory.
    public bool DirectoryExists => System.IO.Directory.Exists(Path);
}

/// <summary>Groups catalog sessions into projects and colours them.</summary>
public static class ProjectCatalog
{
    /// <summary>OpenChamber's own palette, in its declared order.</summary>
    public static readonly IReadOnlyList<ProjectColor> Colors = new[]
    {
        new ProjectColor("keyword",  "Purple", "#34983A"),
        new ProjectColor("string",   "Green",  "#D58373"),
        new ProjectColor("number",   "Pink",   "#279E93"),
        new ProjectColor("type",     "Gold",   "#479CB1"),
        new ProjectColor("constant", "Cyan",   "#C69457"),
        new ProjectColor("comment",  "Muted",  "#728772"),
        new ProjectColor("error",    "Red",    "#DA5B4A"),
        new ProjectColor("primary",  "Blue",   "#DA7C47"),
        new ProjectColor("success",  "Green",  "#76AD4F"),
    };

    /// <summary>Fallback for a project OpenChamber has not coloured.</summary>
    public const string DefaultColorHex = "#728772";

    /// <summary>
    /// The segment that marks OpenChamber's own per-conversation working directories.
    /// </summary>
    private const string ChatsMarker = "\\openchamber\\chats";

    /// <summary>Label for the group those directories collapse into.</summary>
    public const string ChatsGroupLabel = "OpenChamber 会话";

    /// <summary>
    /// The directory a session's path should be GROUPED under, which is not always the
    /// session's own directory.
    /// </summary>
    /// <param name="sessionDirectory">The session's directory, any separator style.</param>
    /// <param name="labelOverride">
    /// A friendlier display name when the directory was rewritten, else null.
    /// </param>
    /// <returns>The directory to group under, already normalised.</returns>
    /// <remarks>
    /// One rule, and it exists because of what the list looked like without it.
    /// <para>
    /// OpenChamber gives every conversation its own working directory:
    /// <c>…\.config\openchamber\chats\2026-01-01\session-…</c>. The catalog
    /// records that directory for the session, so grouping on it produced 25 rows named
    /// <c>session-…</c>, each holding exactly one session, which buried the
    /// handful of directories a person would actually call a project. On this machine
    /// that was 25 of 58 rows.
    /// <para>
    /// Everything at or below <c>openchamber\chats</c> therefore collapses onto the
    /// <c>chats</c> directory itself: one row, all of those sessions, still openable as a
    /// set. Matching on the marker rather than on a <c>session-&lt;uuid&gt;</c> leaf
    /// pattern is deliberate — it needs no knowledge of OpenChamber's id format, and it
    /// cannot miss a variant of it.
    /// </remarks>
    public static string ResolveProjectDirectory(
        string sessionDirectory, out string? labelOverride)
    {
        labelOverride = null;

        var normalized = Normalize(sessionDirectory);
        if (normalized.Length == 0) return normalized;

        var marker = normalized.IndexOf(ChatsMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return normalized;

        // Cut immediately BEFORE "openchamber", so the group is the directory that
        // contains it: …\.config. The alternative, cutting after "chats", would also be
        // correct today but reads as though "chats" were the point.
        var owner = normalized[..marker].TrimEnd('\\');

        // A path that IS the marker itself, with nothing above it, cannot be trimmed to
        // nothing without losing the only directory there is.
        if (owner.Length == 0) return normalized;

        labelOverride = ChatsGroupLabel;
        return owner;
    }

    /// <summary>Resolve a colour key to its hex, or the default when unknown.</summary>
    public static string HexFor(string? key)
        => key is null ? DefaultColorHex
           : Colors.FirstOrDefault(c => c.Key == key)?.Hex ?? DefaultColorHex;

    /// <summary>
    /// Group sessions into projects by directory.
    /// </summary>
    /// <param name="sessions">Catalog sessions.</param>
    /// <param name="existingColors">
    /// Colour keys already assigned by OpenChamber, keyed by project path. Read-only
    /// lookup so a project's colour matches the one the user already sees there.
    /// </param>
    public static IReadOnlyList<ProjectEntry> Group(
        IEnumerable<SessionInfo> sessions,
        IReadOnlyDictionary<string, string>? existingColors = null)
    {
        var grouped = new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Directory)) continue;

            // Not Normalize(s.Directory): a session's directory is not always the
            // project it belongs to. See ResolveProjectDirectory.
            var key = ResolveProjectDirectory(s.Directory, out var label);
            if (key.Length == 0) continue;

            if (!grouped.TryGetValue(key, out var list))
                grouped[key] = list = new List<SessionInfo>();
            list.Add(s);

            // A real leaf name always beats a rule's suggestion, so whichever project
            // is seen first without a rename keeps its own folder name.
            if (label is not null && !labels.ContainsKey(key))
                labels[key] = label;
        }

        // Least-used-first, exactly like OpenChamber's pickAutoColor, but deterministic:
        // OpenChamber picks randomly among the least-used keys, which would give a
        // project a different colour on every app launch.
        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        // The incoming keys are normalised HERE, not required of the caller.
        //
        // The lookup below is by normalised path, so a dictionary keyed the way a
        // human would type it ("F:/p4") simply missed and the project fell back to a
        // fresh colour — the one thing this parameter exists to prevent. OpenChamber
        // itself stores paths with forward slashes while the catalog carries both, so
        // the mismatch was the normal case, not an edge case. The self-test caught it.
        var honour = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingColors is not null)
            foreach (var pair in existingColors)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;

                // Through the same rule, so a colour OpenChamber pinned to one chats
                // subdirectory lands on the collapsed group rather than being orphaned.
                var norm = ResolveProjectDirectory(pair.Key, out _);
                if (norm.Length == 0) continue;

                // First writer wins, so a duplicated path cannot flip-flop per run.
                if (!honour.ContainsKey(norm)) honour[norm] = pair.Value;
                used[pair.Value] = used.TryGetValue(pair.Value, out var n) ? n + 1 : 1;
            }

        var entries = new List<ProjectEntry>(grouped.Count);

        foreach (var pair in grouped)
        {
            var ordered = pair.Value
                .OrderByDescending(s => s.Updated)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();

            string key;
            if (honour.TryGetValue(pair.Key, out var found))
            {
                key = found;
            }
            else
            {
                key = PickLeastUsed(used);
                used[key] = used.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            entries.Add(new ProjectEntry
            {
                Path = pair.Key,
                Label = labels.TryGetValue(pair.Key, out var friendly)
                    ? friendly
                    : LeafName(pair.Key),
                ColorKey = key,
                ColorHex = HexFor(key),
                Sessions = ordered,
            });
        }

        return entries
            .OrderByDescending(p => p.LastUsed)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The colour key nobody is using yet, lowest declared index wins.
    /// </summary>
    private static string PickLeastUsed(IReadOnlyDictionary<string, int> used)
    {
        var best = Colors[0].Key;
        var bestCount = int.MaxValue;

        foreach (var c in Colors)
        {
            used.TryGetValue(c.Key, out var count);
            if (count >= bestCount) continue;
            bestCount = count;
            best = c.Key;
            if (count == 0) break;      // cannot do better
        }

        return best;
    }

    /// <summary>
    /// One directory, one string.
    /// </summary>
    /// <remarks>
    /// Essential, not cosmetic. The catalog mixes separators freely: it contains
    /// "F:/x/y" and "F:\x\y" for the same folder, and both "C:\" and "C:/". Grouping
    /// on the raw string produced 84 projects where 80 is correct, and the duplicates
    /// stole colours from each other, so two halves of one folder showed as two
    /// differently-coloured projects.
    /// </remarks>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim().Replace('/', '\\');

        // A UNC path is canonically "\\server\share", so the prefix is EMITTED, both
        // backslashes and all, and the loop resumes at index 2 with the collapse state
        // already primed.
        //
        // The first version seeded previousWasSeparator with firstIsUnc and let the loop
        // run from 0, which looks right and is wrong: the loop then skipped the leading
        // separator too, so "//srv/share" came out as "srv\share" — a share silently
        // turned into a relative path. Emitting a single "\" fixed that but produced
        // "\srv\share", a root-relative path, which is a different wrong answer. The
        // self-test caught both.
        var firstIsUnc = trimmed.StartsWith("\\\\", StringComparison.Ordinal);
        var sb = new System.Text.StringBuilder(trimmed.Length);
        if (firstIsUnc) sb.Append("\\\\");

        var previousWasSeparator = firstIsUnc;
        for (var i = firstIsUnc ? 2 : 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '\\')
            {
                if (previousWasSeparator) continue;
                previousWasSeparator = true;
            }
            else
            {
                previousWasSeparator = false;
            }
            sb.Append(c);
        }

        var result = sb.ToString();

        // Drop a trailing separator, but never turn a drive root into nothing.
        while (result.Length > 3 && result.EndsWith('\\')) result = result[..^1];

        return result;
    }

    /// <summary>Last segment of a normalised path; a drive root falls back to the drive.</summary>
    public static string LeafName(string normalizedPath)
    {
        var trimmed = normalizedPath.TrimEnd('\\');
        var leaf = trimmed[(trimmed.LastIndexOf('\\') + 1)..];
        return leaf.Length == 0 ? trimmed : leaf;
    }

    /// <summary>Number of assertions checked. Throws on the first failure.</summary>
    public static int RunSelfTest()
    {
        var n = 0;
        void Check(bool ok, string what)
        {
            n++;
            if (!ok) throw new InvalidOperationException("ProjectCatalog self-test failed: " + what);
        }
        void CheckEqual<T>(T expected, T actual, string what)
        {
            n++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(
                    $"ProjectCatalog self-test failed: {what} (expected '{expected}', got '{actual}')");
        }

        // Normalisation: the reason this function exists.
        CheckEqual("F:\\a\\b", Normalize("F:/a/b"), "forward slashes become backslashes");
        CheckEqual("F:\\a\\b", Normalize("F:\\a\\b"), "backslashes are left alone");
        CheckEqual("F:\\a\\b", Normalize("F:/a/b/"), "a trailing separator is dropped");
        CheckEqual("F:\\a\\b", Normalize("F:\\a\\\\b"), "repeated separators collapse");
        CheckEqual("F:\\a\\b", Normalize("  F:/a/b  "), "whitespace is trimmed");
        // A drive root canonicalises to "C:\" WITH its separator kept. Deliberate, and
        // it matches SessionInfo.ProjectPath, which also renders a drive root as "C:\".
        // The first version of this test expected "C:", which is wrong: it would make
        // the drive root sort and display differently from every other path.
        CheckEqual("C:\\", Normalize("C:/"), "a forward-slash drive root gets one backslash");
        CheckEqual("C:\\", Normalize("C:\\"), "a drive root is already canonical");
        CheckEqual("C:\\", Normalize("C:\\\\"), "a doubled drive-root separator collapses to one");
        CheckEqual("\\\\srv\\share", Normalize("//srv/share"), "a UNC prefix survives collapsing");
        CheckEqual(string.Empty, Normalize(""), "an empty path normalises to empty");
        CheckEqual(string.Empty, Normalize("   "), "whitespace normalises to empty");
        CheckEqual("F:\\示例 目录\\a b", Normalize("F:/示例 目录/a b"), "CJK and spaces survive");

        // LeafName.
        CheckEqual("paper", LeafName("F:\\a\\paper"), "leaf of a normal path");
        CheckEqual("paper", LeafName("F:\\a\\paper\\"), "a trailing separator is ignored");
        CheckEqual("C:", LeafName("C:\\"), "a drive root has no leaf, so it names itself");

        // Palette.
        CheckEqual(9, Colors.Count, "the palette has OpenChamber's nine colours");
        CheckEqual("#34983A", HexFor("keyword"), "keyword resolves to the observed hex");
        CheckEqual("#728772", HexFor("comment"), "comment resolves to the observed hex");
        CheckEqual("#DA5B4A", HexFor("error"), "error resolves to the observed hex");
        CheckEqual(DefaultColorHex, HexFor("nope"), "an unknown key falls back");
        CheckEqual(DefaultColorHex, HexFor(null), "a null key falls back");
        CheckEqual("#DA7C47", HexFor("primary"), "primary is OpenChamber's brand orange");

        // Colour assignment: distinct, stable, and least-used-first.
        // SessionInfo's positional signature is (Id, Title, Directory, Agent, Updated,
        // Messages, ProjectPath, RawDirectory); grouping keys off ProjectPath, the
        // native form, so that is what the fixtures set.
        var sessions = new List<SessionInfo>();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 9; i++)
        {
            var dir = $"F:\\p{i}";
            sessions.Add(new SessionInfo($"ses_p{i}", $"T{i}", dir.Replace('\\', '/'),
                                         "build", t0.AddDays(i), 10, dir, dir));
        }

        var projects = ProjectCatalog.Group(sessions);
        CheckEqual(9, projects.Count, "one project per distinct directory");
        CheckEqual(9, projects.Select(p => p.ColorKey).Distinct().Count(),
                   "nine projects get nine distinct colours");

        var again = ProjectCatalog.Group(sessions);
        Check(again.Select(p => p.ColorKey).SequenceEqual(projects.Select(p => p.ColorKey)),
              "colour assignment is deterministic, unlike OpenChamber's random pick");

        // Existing colours win, and are matched by the NORMALISED path.
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["F:\\p3"] = "error",
            ["F:/p4"] = "primary",     // different separators, same folder
        };
        var honoured = ProjectCatalog.Group(sessions, existing).ToDictionary(p => p.Path);
        CheckEqual("error", honoured["F:\\p3"].ColorKey, "an existing colour is honoured");
        CheckEqual("#DA5B4A", honoured["F:\\p3"].ColorHex, "and resolves to its hex");
        CheckEqual("primary", honoured["F:\\p4"].ColorKey,
                   "an existing colour matches across separator styles");

        // Sessions are ordered most recent first, because that is the order the tabs
        // get opened in.
        //
        // The Created values below run BACKWARDS against Updated on purpose. ses_new
        // has the newest Updated but the EARLIEST Created, so a project that took
        // Sessions[^1].Created (i.e. the oldest by Updated) would report a later date
        // than one of its own sessions. Min() is what makes Created mean "when did
        // this project start".
        var multi = ProjectCatalog.Group(new List<SessionInfo>
        {
            new SessionInfo("ses_old", "old", "F:/x", "build", t0, 1, "F:\\x", "F:/x")
                { Created = t0.AddDays(5) },
            new SessionInfo("ses_new", "new", "F:/x", "build", t0.AddDays(5), 1, "F:\\x", "F:/x")
                { Created = t0 },
            new SessionInfo("ses_mid", "mid", "F:/x/", "build", t0.AddDays(2), 1, "F:/x/", "F:/x/")
                { Created = t0.AddDays(2) },
        });
        CheckEqual(1, multi.Count, "separator variants of one folder collapse to one project");
        CheckEqual(3, multi[0].SessionCount, "all three sessions land in the same project");
        CheckEqual("ses_new", multi[0].Sessions[0].Id, "sessions come back newest first");
        CheckEqual("ses_old", multi[0].Sessions[^1].Id, "and oldest last");
        CheckEqual(t0.AddDays(5), multi[0].LastUsed, "LastUsed is the newest session");
        CheckEqual(t0, multi[0].Created,
                   "Created is the earliest creation time, not the oldest Updated");

        // A catalog written before the created column existed carries none, so every
        // session in it reports MinValue. The project must say so rather than invent
        // a date: the UI renders MinValue as an em dash, a 1601 date as a wrong one.
        var undated = ProjectCatalog.Group(new List<SessionInfo>
        {
            new SessionInfo("ses_u1", "u1", "F:/nodate", "build", t0, 1, "F:\\nodate", "F:/nodate"),
            new SessionInfo("ses_u2", "u2", "F:/nodate", "build", t0.AddDays(3), 1, "F:\\nodate", "F:/nodate"),
        });
        CheckEqual(DateTimeOffset.MinValue, undated[0].Created,
                   "a project whose sessions have no creation time reports none");

        // One undated session must not hide the rest. A catalog with a NULL
        // time_created, or a hand-edited one, can mix dated and undated rows in the
        // same project; reporting an em dash then would discard 40 real dates
        // because of one gap.
        var mostlyDated = ProjectCatalog.Group(new List<SessionInfo>
        {
            new SessionInfo("ses_d1", "d1", "F:/mixed", "build", t0.AddDays(9), 1,
                "F:\\mixed", "F:/mixed") { Created = t0.AddDays(1) },
            new SessionInfo("ses_d2", "d2", "F:/mixed", "build", t0.AddDays(8), 1,
                "F:\\mixed", "F:/mixed") { Created = t0.AddDays(2) },
            new SessionInfo("ses_nd", "nd", "F:/mixed", "build", t0.AddDays(7), 1,
                "F:\\mixed", "F:/mixed"),          // no Created: MinValue
        });
        CheckEqual(t0.AddDays(1), mostlyDated[0].Created,
                   "one undated session does not hide the project's real creation date");

        // Ordering of the project list itself.
        var ordered = ProjectCatalog.Group(sessions);
        CheckEqual("p8", ordered[0].Label, "the most recent project comes first");
        CheckEqual("p0", ordered[^1].Label, "the oldest comes last");

        // A session with no usable directory is dropped, not crashed on.
        var noDir = ProjectCatalog.Group(new List<SessionInfo>
        {
            new("ses_a", "a", "", "build", t0, 1, "", ""),
            new("ses_b", "b", "F:/ok", "build", t0, 1, "F:\\ok", "F:/ok"),
        });
        CheckEqual(1, noDir.Count, "a session with no directory is skipped");
        CheckEqual("ok", noDir[0].Label, "the surviving project is the real one");

        CheckEqual(0, ProjectCatalog.Group(Array.Empty<SessionInfo>()).Count,
                   "an empty input yields no projects");

        // The grouping rule for OpenChamber's per-conversation working directories.
        var chatsRoot = "C:\\Users\\demo\\.config";
        var oneChat = chatsRoot + "\\openchamber\\chats\\2026-01-01\\session-abc";

        CheckEqual(chatsRoot,
                   ProjectCatalog.ResolveProjectDirectory(oneChat, out var ruleLabel),
                   "a chats subdirectory groups under the directory above openchamber");
        CheckEqual(ChatsGroupLabel, ruleLabel, "and the rule supplies a friendly label");

        // Forward slashes, since the catalog carries both styles.
        CheckEqual(chatsRoot,
                   ProjectCatalog.ResolveProjectDirectory(
                       "C:/Users/demo/.config/openchamber/chats/2026-01-01/session-abc",
                       out _),
                   "the rule survives forward slashes");

        // A path with no marker is untouched, and asks for no rename.
        CheckEqual("F:\\a\\b",
                   ProjectCatalog.ResolveProjectDirectory("F:\\a\\b", out var plainLabel),
                   "an ordinary project path is left alone");
        CheckEqual(null, plainLabel, "and gets no label override");

        // A user's own folder that happens to be named "chats" is NOT caught: the
        // marker requires the openchamber parent, not just the leaf.
        CheckEqual("F:\\notes\\chats",
                   ProjectCatalog.ResolveProjectDirectory("F:\\notes\\chats", out _),
                   "a folder merely named chats is not an OpenChamber chats tree");

        // Case-insensitive, because Windows paths are.
        CheckEqual(chatsRoot,
                   ProjectCatalog.ResolveProjectDirectory(
                       "C:\\Users\\demo\\.config\\OpenChamber\\Chats\\2026-01-01\\s", out _),
                   "the marker match ignores case");

        // The end-to-end effect: many single-session chat directories become ONE project
        // holding all of them, which is the whole point of the rule.
        var t9 = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var chatSessions = new List<SessionInfo>();
        for (var i = 0; i < 5; i++)
        {
            var dir = chatsRoot + $"\\openchamber\\chats\\2026-09-{10 + i}\\session-{i}";
            chatSessions.Add(new SessionInfo(
                $"ses_c{i}", $"chat {i}", dir.Replace('\\', '/'), "build",
                t9.AddMinutes(i), 4, dir, dir));
        }
        chatSessions.Add(new SessionInfo(
            "ses_real", "real", "F:/real", "build", t9, 9, "F:\\real", "F:/real"));

        var withChats = ProjectCatalog.Group(chatSessions);
        CheckEqual(2, withChats.Count,
                   "five chat directories and one real project make two projects");

        var chatProject = withChats.Single(p => p.Path == chatsRoot);
        CheckEqual(5, chatProject.SessionCount, "all five chats land in one project");
        CheckEqual(ChatsGroupLabel, chatProject.Label, "the collapsed group is labelled");
        Check(withChats.Any(p => p.Path == "F:\\real" && p.Label == "real"),
              "the real project keeps its own folder name");

        // A colour OpenChamber pinned to one chat subdirectory still reaches the group.
        var carried = ProjectCatalog.Group(chatSessions, new Dictionary<string, string>
        {
            [chatsRoot + "\\openchamber\\chats\\2026-09-12\\session-0"] = "error",
        });
        CheckEqual("error", carried.Single(p => p.Path == chatsRoot).ColorKey,
                   "a colour on a chats subdirectory carries to the collapsed group");

        return n;
    }
}
