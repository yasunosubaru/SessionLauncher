// ProjectSort.cs
//
// Ordering for the project list. Kept separate from ProjectCatalog because the
// catalog answers "which projects exist" and this answers "which one first".
// Targets net10.0, BCL only, no WPF.
//
// Every chain ends in an ordinal comparison of Path. That tiebreak is load-bearing:
// OrderBy/OrderByDescending in LINQ are stable sorts, so two projects that tie on
// every visible key come back in INPUT order, and the input order changes from scan
// to scan. Without the final Path comparison the list would reshuffle itself on
// every refresh for reasons the user cannot see.
//
// The keys are ProjectEntry's, which describe a real project (sessions, messages,
// recency) rather than a log-scrape's (visited, lines touched).
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SessionLauncher.App.Models;

namespace SessionLauncher.App.Services;

/// <summary>How the project list is ordered.</summary>
public enum ProjectSortMode
{
    /// <summary>Most recently active first. The default.</summary>
    LastUsed,

    /// <summary>Least recently active first, the exact reverse of
    /// <see cref="LastUsed"/>. Exists so the reverse affordance has a real partner
    /// for the default mode rather than a no-op.</summary>
    LeastUsed,

    /// <summary>Most sessions first, i.e. the deepest project.</summary>
    MostVisited,

    /// <summary>Most messages across all sessions first, a proxy for sheer
    /// workload.</summary>
    MostActive,

    /// <summary>Alphabetical, ignoring case.</summary>
    NameAsc,

    /// <summary>Reverse alphabetical, ignoring case.</summary>
    NameDesc,

    /// <summary>Projects whose earliest session is oldest first, i.e. the stale
    /// tail.</summary>
    Oldest,

    /// <summary>Projects whose earliest session is newest first.</summary>
    Newest,

    /// <summary>Shallowest path first, then name. Keeps a group of same-named leaf
    /// folders (runbook, book, kus, tools) clustered under their parent instead of
    /// scattering them through the list.</summary>
    PathDepth,

    /// <summary>
    /// Projects OpenChamber does not already know come first, then most recently
    /// used.
    /// </summary>
    /// <remarks>
    /// A registered project opens with one deep link and costs nothing; an
    /// unregistered one needs OpenChamber closed, its settings written and itself
    /// relaunched. That is the difference between an instant action and an
    /// interruption, so the projects that need attention lead.
    /// <para>
    /// LAST IN THE ENUM, deliberately. <c>AppSettings.ProjectSort</c> is persisted
    /// as an integer and defaults to <see cref="LastUsed"/>; inserting a member
    /// ahead of it would silently change what every saved setting means.
    /// </para>
    /// </remarks>
    UnregisteredFirst,
}

/// <summary>Applies a <see cref="ProjectSortMode"/> to a project list.</summary>
public static class ProjectSort
{
    /// <summary>Order <paramref name="projects"/> by <paramref name="mode"/>.</summary>
    /// <param name="projects">The projects to order.</param>
    /// <param name="mode">How to order them.</param>
    /// <param name="registeredPaths">
    /// NORMALISED paths of the projects OpenChamber already lists, for
    /// <see cref="ProjectSortMode.UnregisteredFirst"/>. Optional, and ignored by
    /// every other mode: the modes that predate it have no business knowing about
    /// another application's state, and making them read it would mean the list
    /// reorders itself when OpenChamber's project list changes.
    /// </param>
    public static IReadOnlyList<ProjectEntry> Apply(
        IEnumerable<ProjectEntry> projects,
        ProjectSortMode mode,
        IReadOnlySet<string>? registeredPaths = null)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var byPath = StringComparer.Ordinal;
        var byName = StringComparer.OrdinalIgnoreCase;

        IOrderedEnumerable<ProjectEntry>? ordered = mode switch
        {
            ProjectSortMode.LastUsed => projects
                .OrderByDescending(p => p.LastUsed)
                .ThenByDescending(p => p.SessionCount)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.LeastUsed => projects
                .OrderBy(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.MostVisited => projects
                .OrderByDescending(p => p.SessionCount)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.MostActive => projects
                .OrderByDescending(p => p.MessageCount)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.NameAsc => projects
                .OrderBy(p => p.Label, byName)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.NameDesc => projects
                .OrderByDescending(p => p.Label, byName)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.Oldest => projects
                .OrderBy(p => p.FirstUsed)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.Newest => projects
                .OrderByDescending(p => p.FirstUsed)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            ProjectSortMode.PathDepth => projects
                .OrderBy(p => Depth(p.Path))
                .ThenBy(p => p.Label, byName)
                .ThenBy(p => p.Path, byPath),

            // The grouping is the FIRST key, so it beats recency outright: an
            // unregistered project leads even when a registered one was used an
            // hour ago. Registering a project is the rarer, higher-priority task.
            ProjectSortMode.UnregisteredFirst => projects
                .OrderBy(p => registeredPaths is not null
                              && registeredPaths.Contains(ProjectCatalog.Normalize(p.Path))
                    ? 1 : 0)
                .ThenByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),

            _ => projects
                .OrderByDescending(p => p.LastUsed)
                .ThenBy(p => p.Path, byPath),
        };

        return ordered.ToList();
    }

    /// <summary>The logical opposite of <paramref name="mode"/>.</summary>
    /// <remarks>
    /// An involution: <c>Toggle(Toggle(m)) == m</c> for every m, so a UI can offer a
    /// single "reverse" affordance without keeping a second piece of state.
    /// </remarks>
    public static ProjectSortMode Toggle(ProjectSortMode mode) => mode switch
    {
        // Each mode needs a partner that maps BACK to it, or the reverse button walks
        // you off somewhere you cannot get back from. Two conflicts had to be broken
        // to keep the involution, and both are worth stating:
        //   - LastUsed and LeastUsed are the natural pair (LastUsed desc / asc).
        //   - Newest and Oldest are the natural pair (FirstUsed desc / asc).
        // Neither LastUsed nor Newest can also answer for Oldest, which is what the
        // first version did; that made Toggle(Toggle(m)) != m and the self-test caught
        // it. PathDepth has no directional sense, so it is its own partner.
        ProjectSortMode.LastUsed => ProjectSortMode.LeastUsed,
        ProjectSortMode.LeastUsed => ProjectSortMode.LastUsed,
        ProjectSortMode.Oldest => ProjectSortMode.Newest,
        ProjectSortMode.Newest => ProjectSortMode.Oldest,
        ProjectSortMode.MostVisited => ProjectSortMode.MostActive,
        ProjectSortMode.MostActive => ProjectSortMode.MostVisited,
        ProjectSortMode.NameAsc => ProjectSortMode.NameDesc,
        ProjectSortMode.NameDesc => ProjectSortMode.NameAsc,
        ProjectSortMode.PathDepth => ProjectSortMode.PathDepth,
        // No directional sense of its own, so it is its own partner — the same
        // reasoning as PathDepth.
        ProjectSortMode.UnregisteredFirst => ProjectSortMode.UnregisteredFirst,
        _ => ProjectSortMode.LastUsed,
    };

    /// <summary>
    /// Number of separators in the path, counting both kinds and treating a drive
    /// root as depth 0.
    /// </summary>
    public static int Depth(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;

        var depth = 0;
        foreach (var c in path)
        {
            if (c == '\\' || c == '/') depth++;
        }

        // "C:\" has one separator but is the root, not a child of it.
        return depth <= 1 ? 0 : depth - 1;
    }

    /// <summary>Number of assertions checked. Throws on the first failure.</summary>
    public static int RunSelfTest()
    {
        var n = 0;
        void Check(bool ok, string what)
        {
            n++;
            if (!ok) throw new InvalidOperationException("ProjectSort self-test failed: " + what);
        }

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Three disjoint time bands, so "recent" and "ancient" cannot be confused.
        var ancient = (t0, t0.AddDays(1));
        var mid = (t1, t1.AddDays(1));
        var recent = (t3.AddDays(-1), t3);

        // One helper that builds a single row from its sort keys, so every call site
        // below is a plain call rather than a collection initialiser that would read
        // as if each call added one element when it actually adds a List.
        //
        // The project is described the way a real one is: a set of sessions under a
        // directory. band bounds the session timestamps, visits is the session count
        // and lines the total message count.
        //
        // There is deliberately no "name" argument. A project's Label is the LEAF of
        // its path, so a name passed separately would be decorative and could silently
        // disagree with what the list actually renders — which is exactly what the first
        // version of this fixture did, and why its NameAsc assertion read "aaa,bbb,ccc"
        // instead of the names it had just written down. The fixture paths carry the
        // names themselves, and the two that must share a label do it by sitting under
        // different parents.
        ProjectEntry P(
            string path, (DateTimeOffset From, DateTimeOffset To) band, int lines, int visits)
        {
            var sessions = new List<SessionInfo>();
            var label = ProjectCatalog.LeafName(ProjectCatalog.Normalize(path));

            // Spread the messages over the sessions rather than dumping the total onto
            // one of them: with 999 sessions and 100 messages a "put it all on the
            // newest" shortcut produces NEGATIVE per-session counts, which is nonsense
            // data that still sums correctly and so hides itself.
            var per = visits == 0 ? 0 : lines / visits;
            var remainder = lines - per * visits;

            for (var i = 0; i < visits; i++)
            {
                var span = visits == 1 ? 0d : (double)(band.To - band.From).Ticks / (visits - 1);
                var at = band.From.AddTicks((long)(span * i));
                var dir = path.Replace('\\', '/');
                var messages = per + (i == visits - 1 ? remainder : 0);
                sessions.Add(new SessionInfo(
                    $"ses_{i}", $"{label}-{i}", dir, "build", at, Messages: messages,
                    ProjectPath: path, RawDirectory: dir));
            }

            return ProjectCatalog.Group(sessions).Single();
        }

        // Deliberately conflicting values, arranged so every sort mode has a different
        // winner: the most-visited project is the least recently used, the most active
        // is barely visited, two share a leaf name, two share every sort key so only
        // the Path tiebreak can separate them.
        //
        // Note the band always runs forwards. The old log-derived record had two
        // independent timestamps and happily described a project first seen in
        // September and last used in June, which is impossible; ProjectEntry derives
        // both from the same session set, so such a row cannot be built any more and
        // the fixture had to be rebuilt around real time bands.
        var data = new List<ProjectEntry>
        {
            P("Y:\\alpha", recent, 100, 10),    // recent, mid everything
            P("X:\\beta", ancient, 100, 999),   // MOST visited, LEAST recent
            P("X:\\gamma", mid, 9999, 1),       // MOST active, barely visited
            P("W:\\delta", recent, 50, 20),     // recent, second-most visited
            P("V:\\alpha", ancient, 10, 1),     // shares a leaf with Y:\alpha
            P("U:\\epsilon", mid, 10, 1),       // ties with T:\zeta on every key
            P("T:\\zeta", mid, 10, 1),          // ditto
        };

        var ids = ProjectSort.Apply(data, ProjectSortMode.LastUsed)
                             .Select(p => p.Path).ToList();

        // The two recent projects tie on recency, so the session count decides, and
        // delta has twice as many.
        Check(ids[0] == "W:\\delta", "LastUsed breaks the recency tie on session count");
        Check(ids[1] == "Y:\\alpha", "LastUsed second is the other recent one");
        Check(ids[2] == "T:\\zeta",
              "LastUsed continues into the middle band, first by ordinal Path, got " + ids[2]);

        var byVisit = ProjectSort.Apply(data, ProjectSortMode.MostVisited)
                                .Select(p => p.Path).ToList();
        Check(byVisit[0] == "X:\\beta", "MostVisited puts the 999-session project first");
        Check(byVisit[1] == "W:\\delta", "MostVisited second is the 20-session project");

        var byLines = ProjectSort.Apply(data, ProjectSortMode.MostActive)
                                 .Select(p => p.Path).ToList();
        Check(byLines[0] == "X:\\gamma", "MostActive puts the 9999-message project first");

        var asc = ProjectSort.Apply(data, ProjectSortMode.NameAsc).Select(p => p.Label).ToList();
        var desc = ProjectSort.Apply(data, ProjectSortMode.NameDesc).Select(p => p.Label).ToList();
        Check(string.Join(",", asc) == "alpha,alpha,beta,delta,epsilon,gamma,zeta",
              "NameAsc is case-insensitive alphabetical, got " + string.Join(",", asc));

        // The two alphas tie on Label, so NameDesc must still be a strict reversal of
        // the name ordering rather than an arbitrary shuffle of the tied pair.
        Check(desc.Count == asc.Count, "NameDesc keeps every row");
        Check(desc[0] == "zeta" && desc[^1] == "alpha",
              "NameDesc runs zeta first and alpha last, got " + string.Join(",", desc));
        Check(string.Join(",", desc) == "zeta,gamma,epsilon,delta,beta,alpha,alpha",
              "NameDesc is the exact reverse of NameAsc, got " + string.Join(",", desc));

        // Every fixture path is one segment deep, so PathDepth is a total tie and the
        // name ordering decides. That makes it the sharpest test of the tiebreak chain.
        var depth = ProjectSort.Apply(data, ProjectSortMode.PathDepth)
                               .Select(p => p.Path).ToList();
        Check(string.Join(",", depth) ==
                  "V:\\alpha,Y:\\alpha,X:\\beta,W:\\delta,U:\\epsilon,X:\\gamma,T:\\zeta",
              "PathDepth falls through to name then path, got " + string.Join(",", depth));
        Check(ProjectSort.Depth("C:\\") == 0, "a drive root is depth 0");
        Check(ProjectSort.Depth("C:\\a") == 0, "one segment is still depth 0");
        Check(ProjectSort.Depth("C:\\a\\b") == 1, "two segments is depth 1");
        Check(ProjectSort.Depth("C:/a/b/c") == 2, "forward slashes count too");
        Check(ProjectSort.Depth(null) == 0, "a null path is depth 0");

        // A genuinely deeper path must actually sort later, which the all-shallow
        // fixture above cannot show. The leaf of C:\deep\leaf is "leaf", which is the
        // label that comes back.
        var deeper = ProjectSort.Apply(
            new List<ProjectEntry>
            {
                P("C:\\deep\\leaf", mid, 10, 1),
                P("C:\\shallow", mid, 10, 1),
            },
            ProjectSortMode.PathDepth).Select(p => p.Label).ToList();
        Check(string.Join(",", deeper) == "shallow,leaf",
              "a deeper path really does sort later, got " + string.Join(",", deeper));

        // The tiebreak: two projects identical on every visible key.
        var tie = ProjectSort.Apply(
            new List<ProjectEntry>
            {
                P("T:\\zeta", mid, 10, 1),
                P("U:\\epsilon", mid, 10, 1),
            },
            ProjectSortMode.LastUsed).Select(p => p.Path).ToList();
        Check(tie[0] == "T:\\zeta",
              "equal keys fall back to ordinal Path, not input order");

        // Repeating the same call must give the same answer, not just a valid one.
        var repeat = ProjectSort.Apply(
            new List<ProjectEntry>
            {
                P("T:\\zeta", mid, 10, 1),
                P("U:\\epsilon", mid, 10, 1),
            },
            ProjectSortMode.LastUsed).Select(p => p.Path).ToList();
        Check(repeat.SequenceEqual(tie), "the tiebreak is stable across calls");

        // ---- UnregisteredFirst ----
        //
        // Registered projects can be opened with a deep link and cost nothing;
        // unregistered ones need OpenChamber closed, a settings write and a relaunch.
        // That is the difference between an instant action and an interruption, so
        // the projects that need attention lead.

        DateTimeOffset LastUsedOf(List<string> group, string path)
            => data.Single(p => p.Path == path).LastUsed;

        static bool NonIncreasing(IEnumerable<DateTimeOffset> values)
        {
            var list = values.ToList();
            for (var i = 1; i < list.Count; i++)
                if (list[i] > list[i - 1]) return false;
            return true;
        }

        {
            var registered3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Y:\\alpha", "W:\\delta", "X:\\gamma",
            };

            var grouped = ProjectSort.Apply(data, ProjectSortMode.UnregisteredFirst, registered3);
            var head = grouped.Take(4).Select(p => p.Path).ToList();
            var tail = grouped.Skip(4).Select(p => p.Path).ToList();

            // Set membership, not exact order: within a group the order is by
            // recency, and several fixtures share a band, so pinning the sequence
            // here would test the fixture rather than the grouping.
            var expectedUnregistered = new[] { "X:\\beta", "V:\\alpha", "U:\\epsilon", "T:\\zeta" };
            Check(head.OrderBy(s => s, StringComparer.Ordinal)
                      .SequenceEqual(expectedUnregistered.OrderBy(s => s, StringComparer.Ordinal)),
                  "the four unregistered projects lead, got " + string.Join(",", head));
            Check(tail.OrderBy(s => s, StringComparer.Ordinal)
                      .SequenceEqual(registered3.OrderBy(s => s, StringComparer.Ordinal)),
                  "and the three registered ones follow, got " + string.Join(",", tail));

            // Each group is internally recency-ordered, which is what makes the
            // mode a sort rather than a partition.
            Check(NonIncreasing(head.Select(p => LastUsedOf(head, p))),
                  "the unregistered group is ordered by recency descending");
            Check(NonIncreasing(tail.Select(p => LastUsedOf(tail, p))),
                  "the registered group is ordered by recency descending");

            // The grouping must beat recency, and that is the whole point of the mode. The
            // list's leader under plain recency is a registered project; under this
            // mode it no longer leads, with four unregistered projects ahead of it.
            var recencyLeader = ProjectSort.Apply(data, ProjectSortMode.LastUsed)[0].Path;
            Check(registered3.Contains(recencyLeader),
                  "the list's leader under plain recency is a registered project");
            Check(grouped[0].Path != recencyLeader,
                  "under UnregisteredFirst it does not — the grouping outranks recency");

            // No registration data means no reordering: the mode degrades to plain
            // recency rather than inventing an order.
            var unknown = ProjectSort.Apply(data, ProjectSortMode.UnregisteredFirst);
            var byRecency = ProjectSort.Apply(data, ProjectSortMode.LastUsed);
            Check(byRecency[0].Path == unknown[0].Path,
                       "with no registration data the mode leads with the most recent project");
            Check(unknown.Select(p => p.LastUsed)
                         .Zip(unknown.Select(p => p.LastUsed).Skip(1), (a, b) => a >= b)
                         .All(ok => ok),
                  "and the whole list is still ordered by recency descending");

            // The other eight modes must be untouched by the argument.
            foreach (ProjectSortMode m in Enum.GetValues<ProjectSortMode>())
            {
                if (m == ProjectSortMode.UnregisteredFirst) continue;
                Check(ProjectSort.Apply(data, m).Select(p => p.Path)
                          .SequenceEqual(ProjectSort.Apply(data, m, registered3).Select(p => p.Path)),
                      $"{m} ignores the registered set");
            }
        }

        Check(ProjectSort.Toggle(ProjectSortMode.UnregisteredFirst)
                == ProjectSortMode.UnregisteredFirst,
              "UnregisteredFirst is its own reverse, like PathDepth");

        // The new member has to go LAST. AppSettings.ProjectSort defaults to
        // LastUsed and is persisted as an integer, so any member inserted ahead of
        // it would silently change what every existing saved setting means.
        Check(Enum.GetValues<ProjectSortMode>()[0] == ProjectSortMode.LastUsed,
                   "LastUsed is still the enum's zero value");

        // Every mode ends in the Path tiebreak, so no mode can leak input order.
        foreach (ProjectSortMode m in Enum.GetValues<ProjectSortMode>())
        {
            var once = ProjectSort.Apply(data, m).Select(p => p.Path).ToList();

            // AsEnumerable() is required: List<T> has an in-place void Reverse(), and
            // that instance method wins overload resolution against the LINQ one, so a
            // bare data.Reverse() would hand Apply() a void.
            var reversed = data.AsEnumerable().Reverse().ToList();
            var twice = ProjectSort.Apply(reversed, m).Select(p => p.Path).ToList();
            Check(once.SequenceEqual(twice),
                  $"{m} is independent of input order");

            var perm = once.OrderBy(s => s, StringComparer.Ordinal).ToList();
            Check(perm.Count == data.Count, $"{m} neither drops nor duplicates rows");
        }

        // Toggle is an involution over the whole enum.
        foreach (ProjectSortMode m in Enum.GetValues<ProjectSortMode>())
            Check(ProjectSort.Toggle(ProjectSort.Toggle(m)) == m,
                  $"Toggle is an involution for {m}");

        Check(ProjectSort.Toggle(ProjectSortMode.NameAsc) == ProjectSortMode.NameDesc,
              "NameAsc toggles to NameDesc");
        Check(ProjectSort.Toggle(ProjectSortMode.LastUsed) == ProjectSortMode.LeastUsed,
              "LastUsed toggles to LeastUsed, its exact reverse");
        Check(ProjectSort.Toggle(ProjectSortMode.Newest) == ProjectSortMode.Oldest,
              "Newest toggles to Oldest, its exact reverse");

        // The default must be usable and not throw on an empty list.
        Check(ProjectSort.Apply(Array.Empty<ProjectEntry>(), ProjectSortMode.LastUsed).Count == 0,
              "an empty list stays empty");

        return n;
    }
}
