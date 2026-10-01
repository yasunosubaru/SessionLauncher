// WorkspaceService.cs
//
// "Open workspace" for SessionLauncher.
//
// WHAT A WORKSPACE IS HERE, and why it is not opencode's own concept:
//   opencode has no workspace concept. `opencode debug scrap` is documented as
//   "list all known projects", and every entry is {id, worktree, sandboxes} - the
//   worktree IS the project. There is no workspace flag anywhere in `opencode --help`,
//   `session --help` or `debug --help`.
//   OpenChamber does have a workspace concept, but it is a VS Code workspace: its
//   bundle calls `vscode.addWorkspaceFolder(...)` and `syncVSCodeWorkspaceFolder(...)`.
//   And its custom protocol only understands three verbs - openchamber://connect,
//   openchamber://focus and openchamber://session. There is no project or workspace
//   deep link, so this cannot be reached that way.
//
// So a workspace is a VS Code multi-root workspace: a .code-workspace file with a
// "folders" array. Two ways to get one, both implemented below:
//   1. an existing .code-workspace file found near one of the known projects
//   2. one generated on the fly from a set of project folders, which is what makes
//      "open workspace" useful for a multi-project selection
//
// Verified on this machine: VS Code 1.137.0 at
// %LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd, and real .code-workspace
// files exist under F:\.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionLauncher.App.Services;

/// <summary>One openable VS Code workspace.</summary>
public sealed class WorkspaceInfo
{
    /// <summary>Full path to the .code-workspace file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name without the extension, shown in the list.</summary>
    public required string Name { get; init; }

    /// <summary>Directory containing the file.</summary>
    public required string Directory { get; init; }

    /// <summary>Root folders declared by the file, when it could be parsed.</summary>
    public required IReadOnlyList<string> Folders { get; init; }

    /// <summary>False when the file has gone away since discovery.</summary>
    public bool Exists => File.Exists(FilePath);
}

/// <summary>Finds, generates and opens VS Code workspaces.</summary>
public static class WorkspaceService
{
    /// <summary>Extension of a VS Code workspace file, without the dot.</summary>
    public const string Extension = ".code-workspace";

    /// <summary>
    /// How deep to look for a workspace file inside a project directory.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. These directories include F:\ drive roots and network
    /// shares; an unbounded walk would hang the UI. Two levels is enough because
    /// workspace files live beside the project they describe.
    /// </remarks>
    public const int MaxSearchDepth = 2;

    /// <summary>
    /// Find .code-workspace files in or just under the given directories.
    /// </summary>
    /// <param name="directories">Candidate project directories.</param>
    /// <param name="ct">Cancellation.</param>
    public static IReadOnlyList<WorkspaceInfo> Discover(
        IEnumerable<string> directories, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<WorkspaceInfo>();

        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir)) continue;

            foreach (var file in Walk(dir, MaxSearchDepth, ct))
            {
                if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(file)) continue;

                found.Add(Describe(file));
            }
        }

        // Deterministic: same input, same order, so the list does not reshuffle.
        return found
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Read a workspace file into a <see cref="WorkspaceInfo"/>.</summary>
    /// <remarks>
    /// A malformed file must not throw: these are user files that may be mid-edit, and
    /// a launcher that refuses to start over one bad JSON file is useless. An unreadable
    /// file still yields an entry, just with no folders listed.
    /// </remarks>
    public static WorkspaceInfo Describe(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);

        List<string> folders = new();
        try
        {
            if (File.Exists(filePath))
            {
                using var doc = JsonDocument.Parse(
                    File.ReadAllText(filePath, Encoding.UTF8),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                if (doc.RootElement.TryGetProperty("folders", out var array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("path", out var path)
                            && path.ValueKind == JsonValueKind.String)
                        {
                            folders.Add(path.GetString() ?? string.Empty);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the entry; the file is simply reported with no folders.
        }

        return new WorkspaceInfo
        {
            FilePath = filePath,
            Name = name,
            Directory = Path.GetDirectoryName(filePath) ?? string.Empty,
            Folders = folders,
        };
    }

    /// <summary>
    /// Write a multi-root workspace naming the given folders, and return its path.
    /// </summary>
    /// <remarks>
    /// Written to the user's opencode temp dir rather than beside the projects: the
    /// folders can live on different drives, and there is no single directory that
    /// belongs to all of them. Overwrites the same file so repeated selections of the
    /// same set do not litter the disk.
    /// </remarks>
    public static string Create(IEnumerable<string> folders, string? name = null)
    {
        var list = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            throw new ArgumentException("a workspace needs at least one folder", nameof(folders));

        var stem = string.IsNullOrWhiteSpace(name) ? "SessionLauncher" : name.Trim();
        var safe = new StringBuilder(stem.Length);
        foreach (var c in stem)
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SessionLauncher", "workspaces");
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, safe + Extension);

        var root = new JsonObject
        {
            ["folders"] = new JsonArray(
                list.Select(f => (JsonNode)new JsonObject { ["path"] = f }).ToArray()),
        };

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                          Encoding.UTF8);
        return file;
    }

    /// <summary>
    /// Collect candidate .code-workspace paths under a directory, depth-limited.
    /// </summary>
    /// <remarks>
    /// Returns a list rather than yielding, because C# forbids <c>yield return</c>
    /// inside a try block that has a catch clause (CS1626) - and swallowing IO
    /// failures is the whole point here, since these roots include network drives
    /// and disconnected volumes that can block on the very first stat.
    /// </remarks>
    private static List<string> Walk(string root, int maxDepth, CancellationToken ct)
    {
        var results = new List<string>();
        if (maxDepth < 0) return results;

        string[] files;
        try
        {
            if (!Directory.Exists(root)) return results;
            files = Directory.GetFiles(root, "*" + Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return results;
        }

        results.AddRange(files);
        if (maxDepth == 0) return results;

        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return results;
        }

        foreach (var dir in children)
        {
            ct.ThrowIfCancellationRequested();

            // Skip reparse points: junctions and symlinked drives are exactly how a
            // depth-2 walk turns into an unbounded one.
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            results.AddRange(Walk(dir, maxDepth - 1, ct));
        }

        return results;
    }

    /// <summary>Number of assertions checked. Throws on the first failure.</summary>
    public static int RunSelfTest()
    {
        var n = 0;
        void Check(bool ok, string what)
        {
            n++;
            if (!ok) throw new InvalidOperationException("WorkspaceService self-test failed: " + what);
        }

        var temp = Path.Combine(Path.GetTempPath(), "sl-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Round trip: write a two-folder workspace, read it back. Note Create
            // writes to %LOCALAPPDATA%, NOT to `temp`, so it cannot be used to test
            // discovery; discovery gets its own files under `temp`.
            var f1 = Path.Combine(temp, "alpha");
            var f2 = Path.Combine(temp, "beta");
            Directory.CreateDirectory(f1);
            Directory.CreateDirectory(f2);

            var file = Create(new[] { f2, f1, f2 }, "roundtrip");
            Check(File.Exists(file), "Create writes the file");
            Check(file.EndsWith(Extension, StringComparison.Ordinal), "Create appends the extension");
            Check(!file.Contains("roundtrip.code-workspace.code-workspace"),
                  "the stem is not double-suffixed");

            var info = Describe(file);
            CheckEqual(2, info.Folders.Count, "duplicate folders are collapsed");
            Check(info.Folders.Contains(f1, StringComparer.OrdinalIgnoreCase), "first folder is kept");
            Check(info.Folders.Contains(f2, StringComparer.OrdinalIgnoreCase), "second folder is kept");
            Check(info.Folders[0].CompareTo(info.Folders[1], StringComparison.OrdinalIgnoreCase) < 0,
                  "folders are written in a stable sorted order");
            CheckEqual("roundtrip", info.Name, "the name drops the extension");
            Check(info.Exists, "a freshly written file exists");
            File.Delete(file);

            // Discovery: a top-level file.
            var top = Path.Combine(temp, "top" + Extension);
            File.WriteAllText(top, """{"folders":[{"path":"."}]}""");
            var found = Discover(new[] { temp });
            CheckEqual(1, found.Count, "Discover finds a top-level file");
            CheckEqual(top, found[0].FilePath, "Discover returns the right path");
            CheckEqual(1, found[0].Folders.Count, "a real file's folders are parsed");

            // Discovery: one level down, still within budget.
            var nestedDir = Path.Combine(temp, "sub", "deeper");
            Directory.CreateDirectory(nestedDir);
            var nestedFile = Path.Combine(nestedDir, "nested" + Extension);
            File.WriteAllText(nestedFile, "{}");
            var found2 = Discover(new[] { temp });
            CheckEqual(2, found2.Count, "Discover finds a nested file within the depth budget");
            Check(found2.Select(w => w.FilePath).Contains(nestedFile, StringComparer.OrdinalIgnoreCase),
                  "the nested file is the one found");

            // Beyond the depth budget: must NOT be found, or the walk is unbounded.
            var tooDeep = Path.Combine(temp, "a", "b", "c");
            Directory.CreateDirectory(tooDeep);
            var deepFile = Path.Combine(tooDeep, "deep" + Extension);
            File.WriteAllText(deepFile, "{}");
            Check(!Discover(new[] { temp }).Select(w => w.FilePath)
                        .Contains(deepFile, StringComparer.OrdinalIgnoreCase),
                  "a file past the depth budget is not discovered");
            File.Delete(deepFile);

            // Determinism.
            var again = Discover(new[] { temp });
            Check(again.Select(w => w.FilePath).SequenceEqual(found2.Select(w => w.FilePath)),
                  "Discover is deterministic across calls");

            // A malformed file must not throw; it is still listed.
            var broken = Path.Combine(temp, "broken" + Extension);
            File.WriteAllText(broken, "{ this is not json");
            var withBroken = Discover(new[] { temp });
            CheckEqual(3, withBroken.Count, "a malformed file is still discovered");
            CheckEqual(0, Describe(broken).Folders.Count, "a malformed file reports no folders");

            // A missing directory is skipped, not fatal.
            CheckEqual(0, Discover(new[] { Path.Combine(temp, "does-not-exist") }).Count,
                       "a missing directory yields nothing");
            CheckEqual(0, Discover(Array.Empty<string>()).Count, "an empty input yields nothing");

            // Zero folders is a real error, not an empty workspace.
            Check(Throws(() => Create(Array.Empty<string>())),
                  "Create rejects an empty folder list");

            // Invalid filename characters in the name are replaced, not thrown on.
            var odd = Create(new[] { f1 }, "a<b>c:d");
            Check(File.Exists(odd), "Create survives invalid filename characters");
            Check(!Path.GetFileName(odd).Contains('<'), "the invalid character was replaced");
            File.Delete(odd);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }

        return n;

        void CheckEqual<T>(T expected, T actual, string what)
        {
            n++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(
                    $"WorkspaceService self-test failed: {what} (expected '{expected}', got '{actual}')");
        }

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
    }
}
