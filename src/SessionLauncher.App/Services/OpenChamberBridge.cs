// OpenChamberBridge.cs
//
// Reads and drives OpenChamber's own project state, so "open project" means
// open it IN OPENCHAMBER rather than spawning a terminal somewhere.
//
// WHY THIS FILE EXISTS, and the evidence behind every claim
//
// openchamber:// cannot do it. The only protocol literals in app.asar are
// openchamber://connect, openchamber://focus and openchamber://session, and the
// bundle's other entry point, parseDeepLinkHash, is a GitHub-style
// operations/operations-tag scroll anchor that opens nothing. So a project cannot
// be selected by deep link.
//
// A project is a record in
//     %USERPROFILE%\.config\openchamber\settings.json
// under "projects", an array of objects shaped exactly:
//     { id, path, label?, color?, addedAt, lastOpenedAt, sidebarCollapsed }
// and the one on screen is whichever record "activeProjectId" names.
//
// The id scheme, confirmed by decoding every record inspected:
//     id = "path_" + base64url(pathWithForwardSlashes)
//   path_RDovUHJvamVjdHM                 -> D:/Projects
//   path_QzovVXNlcnMvZGVtbw              -> C:/Users/demo
//   path_RjovZXhhbXBsZS9wZXJzb25hbCBjb250ZW50L2NvdXJzZS1ub3Rlcy90aGVzaXM -> F:/示例/资料/sample
// So it is computed, not opaque: any path can be projected into the scheme. That
// matters twice over — the launcher can synthesise an id for a directory OpenChamber
// has never seen, and an id read back out of settings.json is a readable path
// rather than an opaque token.
//
// A WORKSPACE is the other thing the user asked for, and the sidebar decides what
// it is. settings.json carries:
//     sidebarSessionGroupingMode = "by-worktree"
//     sidebarProjectDisplayMode  = "all"
//     sidebarProjectSortOrder    = "manual"
//     sidebarWorktreeSortOrder   = "recent"
// "by-worktree" means the sessions under a project node in the UI are grouped by
// their directory, so the conversations that share one directory gather into one
// child node under the project. A workspace is therefore a PROJECT together with its
// worktree grouping, and the honest way to open one is to open the project and let
// the sidebar do the grouping.
//
// Writing settings.json is the only handle. That is a destructive-on-crash risk and
// is mitigated, not wished away: see WriteSettings.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SessionLauncher.App.Services;

/// <summary>One project as OpenChamber stores it.</summary>
public sealed class OpenChamberProject
{
    /// <summary>OpenChamber's identifier, i.e. <c>path_</c> + base64url of the path.</summary>
    public required string Id { get; init; }

    /// <summary>Absolute path, in Windows backslash form as OpenChamber writes it.</summary>
    public required string Path { get; init; }

    /// <summary>Display name. OpenChamber leaves this absent for some records.</summary>
    public string? Label { get; init; }

    /// <summary>Optional accent name, e.g. "comment" or "number".</summary>
    public string? Color { get; init; }

    /// <summary>Epoch milliseconds.</summary>
    public long AddedAt { get; init; }

    /// <summary>Epoch milliseconds.</summary>
    public long LastOpenedAt { get; init; }

    /// <summary>Whether the sidebar node is folded shut.</summary>
    public bool SidebarCollapsed { get; init; }
}

/// <summary>Reads and rewrites OpenChamber's project list.</summary>
public static class OpenChamberBridge
{
    /// <summary>Prefix every OpenChamber project id carries.</summary>
    public const string IdPrefix = "path_";

    /// <summary>Where OpenChamber keeps its settings, relative to the user profile.</summary>
    public const string SettingsRelativePath =
        @".config\openchamber\settings.json";

    /// <summary>Settings candidates, first hit wins.</summary>
    public static IReadOnlyList<string> SettingsCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new[]
        {
            Path.Combine(home, ".config", "openchamber", "settings.json"),
            Path.Combine(appData, "openchamber", "settings.json"),
        };
    }

    /// <summary>Locate the settings file, or null when OpenChamber is not set up.</summary>
    public static string? ResolveSettingsPath()
        => SettingsCandidates().FirstOrDefault(File.Exists);

    /// <summary>
    /// Project the path into OpenChamber's id scheme.
    /// </summary>
    /// <remarks>
    /// Forward slashes, no trailing separator, then base64url WITHOUT padding.
    /// Padding is the detail that matters: a '=' would produce an id OpenChamber
    /// never generates, and the record would sit in the list unreferenced.
    /// </remarks>
    public static string MakeId(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("a project path is required", nameof(path));

        var normalised = path.Trim().Replace('\\', '/').TrimEnd('/');
        if (normalised.Length == 0)
            throw new ArgumentException("a project path is required", nameof(path));

        return IdPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(normalised))
                                 .TrimEnd('=')
                                 .Replace('+', '-')
                                 .Replace('/', '_');
    }

    /// <summary>Reverse of <see cref="MakeId"/>; null when the id is not in the scheme.</summary>
    public static string? DecodeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;

        var body = id[IdPrefix.Length..];
        if (body.Length == 0) return null;

        // Put the padding back before decoding; a length that is not a multiple of 4
        // cannot have come from this scheme.
        var padded = body.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };

        try
        {
            var bytes = Convert.FromBase64String(padded);
            var text = Encoding.UTF8.GetString(bytes);
            return text.Length == 0 ? null : text;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Read OpenChamber's projects, or an empty list when it has none yet.</summary>
    public static IReadOnlyList<OpenChamberProject> ReadProjects()
    {
        var path = ResolveSettingsPath();
        if (path is null) return Array.Empty<OpenChamberProject>();

        return ReadProjectsFrom(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Parse the projects array out of a settings document.</summary>
    /// <remarks>
    /// Total on purpose: settings.json is rewritten by a live app, so a read can catch
    /// it mid-write. A launcher must not die because OpenChamber was saving.
    /// </remarks>
    public static IReadOnlyList<OpenChamberProject> ReadProjectsFrom(string json)
    {
        var result = new List<OpenChamberProject>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (root?["projects"] is not JsonArray array) return result;

        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;
            var id = ReadString(obj["id"]);
            var dir = ReadString(obj["path"]);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(dir)) continue;

            result.Add(new OpenChamberProject
            {
                Id = id,
                Path = dir,
                Label = ReadString(obj["label"]),
                Color = ReadString(obj["color"]),
                AddedAt = ReadLong(obj["addedAt"]),
                LastOpenedAt = ReadLong(obj["lastOpenedAt"]),
                SidebarCollapsed = ReadBool(obj["sidebarCollapsed"]),
            });
        }

        return result;
    }

    /// <summary>
    /// True when OpenChamber already lists this project.
    /// </summary>
    /// <param name="projectPath">The project's directory, in any separator style.</param>
    /// <param name="known">
    /// Projects to search instead of reading settings.json. Supplying this keeps a
    /// caller that already has the list from re-reading the file, and lets the
    /// self-test exercise the comparison without touching the live document.
    /// </param>
    /// <remarks>
    /// The comparison goes through <see cref="ProjectCatalog.Normalize"/> on both
    /// sides, and that is not tidiness. OpenChamber stores paths with forward
    /// slashes; the catalog carries "F:/x/y" and "F:\x\y" for the same folder. A raw
    /// string compare misses almost every project, which is exactly what happened to
    /// the colour lookup that shares this normalisation.
    /// </remarks>
    public static bool IsRegistered(string projectPath, IReadOnlyList<OpenChamberProject>? known = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return false;

        var wanted = ProjectCatalog.Normalize(projectPath);
        if (wanted.Length == 0) return false;

        var projects = known ?? ReadProjects();
        return projects.Any(p => ProjectCatalog.Normalize(p.Path).Equals(
            wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// How many keys the document's root object carries; 0 when it will not parse.
    /// </summary>
    /// <remarks>
    /// Taken before a write and compared after, this is the cheapest available
    /// evidence that an edit did not replace the document. OpenChamber's settings
    /// hold 58 top-level keys on this machine, and losing any of them is a silent,
    /// permanent change to someone else's configuration.
    /// </remarks>
    public static int CountTopLevelKeys(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        try
        {
            return JsonNode.Parse(json) is JsonObject obj ? obj.Count : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>How many backups are kept before the oldest is pruned.</summary>
    public const int BackupKeepCount = 10;

    /// <summary>The directory backups are written to by default.</summary>
    public static string DefaultBackupRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SessionLauncher", "backups");

    /// <summary>
    /// Copy the settings file aside, then prune the backup directory.
    /// </summary>
    /// <param name="settingsPath">The file to copy.</param>
    /// <param name="backupRoot">
    /// Destination directory, created if absent. Defaults to
    /// <see cref="DefaultBackupRoot"/>; the self-test passes a scratch directory so
    /// it never fills the real one.
    /// </param>
    /// <returns>The backup's full path.</returns>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="settingsPath"/> does not exist. Copying a missing file would
    /// produce a backup of nothing, which is worse than no backup at all.
    /// </exception>
    /// <remarks>
    /// Called BEFORE every write, not after. The point of a backup is to exist before
    /// the thing it protects against happens.
    /// </remarks>
    public static string BackupSettings(string settingsPath, string? backupRoot = null)
    {
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException(
                "OpenChamber settings.json was not found; cannot back it up.", settingsPath);

        var root = backupRoot ?? DefaultBackupRoot;
        Directory.CreateDirectory(root);

        // The name has to sort lexicographically BY TIME, because pruning below
        // orders by name and must not depend on file timestamps or on the clock
        // not having jumped. It also has to be UNIQUE: a millisecond stamp alone
        // is not, and File.Copy(overwrite: true) then silently collapses two
        // backups into one. Two writes inside the same millisecond lose the first
        // copy, which is exactly the copy you would want if the second write went
        // wrong. It does not bite in normal use — writes are seconds apart — but
        // it made the self-test flaky: 13 rapid writes produced 8 files.
        //
        // A zero-padded sequence appended to every name keeps both properties.
        // Within one millisecond the sequence orders the copies; across
        // milliseconds the stamp dominates; and '-' (0x2D) sorts below every
        // character the stamp itself can contain, so the ordering is total.
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = NextFreeBackupPath(root, stamp);

        File.Copy(settingsPath, target, overwrite: false);

        // Newest first, by name. The stamp format sorts lexicographically, so this
        // needs no file timestamps and cannot reorder on a clock change.
        //
        // The OLDEST is never pruned, however many writes follow it. That copy is the
        // document as it was before this feature ever touched it, so it is the only
        // one that can undo all of them at once — pruning it would eventually leave a
        // backup set that could only roll forward.
        var all = Directory
            .GetFiles(root, "settings-*.json")
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var doomed = all.Skip(BackupKeepCount).ToList();
        if (all.Count > 0) doomed.Remove(all[^1]);

        foreach (var old in doomed)
        {
            // A backup someone has open is left alone; losing one is not fatal.
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return target;
    }

    /// <summary>
    /// First unused backup name for this millisecond, as
    /// <c>settings-&lt;stamp&gt;-&lt;seq&gt;.json</c> with a zero-padded sequence.
    /// </summary>
    /// <remarks>
    /// Only the count of files sharing the stamp matters, and that is bounded by
    /// <see cref="BackupKeepCount"/>, so the scan is trivially short.
    /// </remarks>
    private static string NextFreeBackupPath(string root, string stamp)
    {
        for (var seq = 0; ; seq++)
        {
            var candidate = Path.Combine(root, $"settings-{stamp}-{seq:D3}.json");
            if (!File.Exists(candidate)) return candidate;
            if (seq > BackupKeepCount + 2)
                throw new IOException(
                    $"Refusing to invent a backup name: {seq} files already share the stamp {stamp}.");
        }
    }

    /// <summary>
    /// Prove that a write landed: the project is listed, it is the active one, the
    /// document parses, and nothing was dropped.
    /// </summary>
    /// <param name="settingsPath">The settings file just written.</param>
    /// <param name="projectId">The id <see cref="Activate"/> said it activated.</param>
    /// <param name="minimumKeyCount">
    /// The key count observed BEFORE the write. A smaller count afterwards means the
    /// write lost configuration.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Any of the four conditions fails. Every failure is reported at once rather
    /// than one per call, because the commonest cause — a write that produced an
    /// entirely different document — fails all of them, and fixing one is pointless.
    /// </exception>
    public static void VerifyRegistered(string settingsPath, string projectId, int minimumKeyCount)
    {
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException(
                "OpenChamber settings.json disappeared during the write.", settingsPath);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)) as JsonObject
                   ?? throw new InvalidDataException(
                       $"OpenChamber settings.json is not a JSON object after the write: {settingsPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"OpenChamber settings.json does not parse after the write: {settingsPath}", ex);
        }

        var listed = root["projects"] is JsonArray array
            ? array.OfType<JsonObject>()
                     .Select(p => ReadString(p["id"]))
                     .Where(id => id is not null)
                     .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string?>(StringComparer.Ordinal);

        var problems = new List<string>();

        if (!listed.Contains(projectId))
            problems.Add($"project '{projectId}' is not in the projects array");

        var active = ReadString(root["activeProjectId"]);
        if (active != projectId)
            problems.Add($"activeProjectId is '{active ?? "(absent)"}', expected '{projectId}'");

        if (root.Count < minimumKeyCount)
            problems.Add($"the document has {root.Count} top-level keys, expected at least {minimumKeyCount}");

        if (problems.Count > 0)
            throw new InvalidDataException(
                "OpenChamber settings.json was not left as intended: " + string.Join("; ", problems));
    }

    /// <summary>
    /// Ensure a project record exists and make it the active one.
    /// </summary>
    /// <param name="projectPath">The project's directory.</param>
    /// <param name="label">Display name; the leaf folder name when null.</param>
    /// <param name="colorKey">
    /// One of OpenChamber's palette keys. Applied on create and on update, so a
    /// project this launcher registers arrives with the same accent it shows here.
    /// </param>
    /// <param name="settingsPath">
    /// Which settings file to edit. Defaults to <see cref="ResolveSettingsPath"/>;
    /// the self-test points this at a scratch file so it never edits the live one.
    /// </param>
    /// <returns>The id that was activated.</returns>
    /// <remarks>
    /// The record is created if absent, with the leaf folder name as the label,
    /// because a project with no label renders as a bare path in the sidebar.
    /// <para>
    /// <b>This rewrites a live app's settings file.</b> The write is atomic (temp file
    /// plus replace) so OpenChamber never sees a truncated document, but it is a
    /// read-modify-write against a file a running Electron process owns, with no
    /// locking — so a concurrent OpenChamber save can be clobbered, and an OpenChamber
    /// save afterwards will overwrite this. It is therefore only safe when
    /// OpenChamber is NOT running; establishing that is the caller's job, and
    /// <see cref="VerifyRegistered"/> is what proves the result afterwards.
    /// </para>
    /// </remarks>
    public static string Activate(
        string projectPath, string? label = null, string? colorKey = null, string? settingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("a project path is required", nameof(projectPath));

        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project path not found: {projectPath}");

        var settings = settingsPath
            ?? ResolveSettingsPath()
            ?? throw new FileNotFoundException(
                "OpenChamber settings.json was not found; is OpenChamber installed?");

        var id = MakeId(projectPath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var json = File.ReadAllText(settings, Encoding.UTF8);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("OpenChamber settings.json is not a JSON object");

        if (root["projects"] is not JsonArray projects)
        {
            projects = new JsonArray();
            root["projects"] = projects;
        }

        var existing = projects
            .OfType<JsonObject>()
            .FirstOrDefault(p => p["id"]?.GetValue<string>() == id);

        if (existing is null)
        {
            var created = new JsonObject
            {
                ["id"] = id,
                ["path"] = projectPath,
                ["label"] = label ?? LeafName(projectPath),
                ["addedAt"] = now,
                ["lastOpenedAt"] = now,
                ["sidebarCollapsed"] = false,
            };

            // Omitted entirely rather than written empty: OpenChamber treats a
            // missing colour as "pick one", and an empty string is not a palette key.
            if (!string.IsNullOrWhiteSpace(colorKey)) created["color"] = colorKey;

            projects.Add(created);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(label)) existing["label"] = label;
            if (!string.IsNullOrWhiteSpace(colorKey)) existing["color"] = colorKey;
            existing["lastOpenedAt"] = now;
            // A project you just asked to open should not stay folded in the sidebar.
            existing["sidebarCollapsed"] = false;
        }

        root["activeProjectId"] = id;

        // Deliberately NOT writing sidebarSessionGroupingMode. It is still sitting in
        // this machine's settings.json as "by-worktree", which made it look like the
        // sidebar shape is something to configure. It is not: OpenChamber's own source
        // (useSessionDisplayStore.migrateSessionDisplayState, v6) deletes
        // sessionGroupingMode outright, because "the projects view always groups by
        // worktree". Grepping the bundle for the key returns nothing. Writing it would
        // be a no-op dressed up as a feature.
        root["sidebarProjectDisplayMode"] = "all";

        WriteSettings(settings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    /// <summary>The last directory OpenChamber had open, or null.</summary>
    public static string? ActiveProjectPath()
    {
        var path = ResolveSettingsPath();
        if (path is null) return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            var id = root?["activeProjectId"]?.GetValue<string>();
            return id is null ? null : DecodeId(id);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Last path segment, used as a default label.</summary>
    public static string LeafName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        // A drive root has no leaf; showing "C:\" is more useful than an empty label.
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>Write the settings document atomically.</summary>
    /// <remarks>
    /// <b>This was not atomic until it was changed to use <see cref="File.Replace"/>.</b>
    /// It used to <c>File.Delete(path)</c> then <c>File.Move(temp, path)</c>, under a
    /// comment claiming that replace was atomic on NTFS while never calling it. Between
    /// those two lines the user's entire OpenChamber configuration does not exist: a
    /// second instance, a scanner holding the temp file, or a crash in the window loses
    /// it, and OpenChamber then starts on defaults.
    /// <para>
    /// <see cref="File.Replace"/> swaps the destination's contents in one filesystem
    /// operation, so there is no window in which the file is missing. It throws when
    /// the destination does not exist, which is why the two branches are separate —
    /// the first write into a fresh profile has nothing to replace.
    /// </para>
    /// </remarks>
    private static void WriteSettings(string path, string content)
    {
        var temp = path + ".sessionlauncher.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            if (File.Exists(path))
            {
                // Replace preserves the destination's identity, which matters because
                // another process may already hold a handle to it.
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // Never leave the scratch file behind: it sits next to the real settings
            // and a later run would trip over it.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Put a backup back over the live settings file.
    /// </summary>
    /// <param name="backupPath">A file produced by <see cref="BackupSettings"/>.</param>
    /// <param name="settingsPath">The live settings file to replace.</param>
    /// <returns>
    /// True when the file was restored. False — never an exception — when the backup
    /// is missing, unreadable, or is not a settings document.
    /// </returns>
    /// <remarks>
    /// This is what makes <see cref="BackupSettings"/> worth taking.
    /// <see cref="VerifyRegistered"/> runs AFTER a write and can therefore only report
    /// that something went wrong; without a restore step the backup is a file nobody
    /// ever reads, and the detection buys nothing.
    /// <para>
    /// A backup that does not parse is refused rather than copied: the one moment this
    /// is called is the moment the file is already broken, and overwriting it with
    /// different rubbish helps nobody.
    /// </para>
    /// </remarks>
    public static bool RestoreSettings(string backupPath, string settingsPath)
    {
        if (!File.Exists(backupPath)) return false;

        string content;
        try { content = File.ReadAllText(backupPath, Encoding.UTF8); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        if (CountTopLevelKeys(content) == 0) return false;

        try
        {
            WriteSettings(settingsPath, content);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Read a string field, or null when it is absent or of another type.
    /// </summary>
    /// <remarks>
    /// NOT <c>GetValue&lt;string&gt;()</c>: that throws InvalidOperationException when the
    /// JSON value is a number, and these files belong to a live application, so a
    /// hand-edited or differently-serialised field must not take the launcher down with
    /// it. A non-string is simply "no value".
    /// </remarks>
    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;

        try
        {
            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null) return false;

        try
        {
            return node.GetValueKind() == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is null) return 0;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<long>();
            if (node.GetValueKind() == JsonValueKind.String
                && long.TryParse(node.GetValue<string>(), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // A field of an unexpected shape is not worth failing a settings read over.
        }
        return 0;
    }

    /// <summary>Number of assertions checked. Throws on the first failure.</summary>
    public static int RunSelfTest()
    {
        var n = 0;
        void Check(bool ok, string what)
        {
            n++;
            if (!ok) throw new InvalidOperationException("OpenChamberBridge self-test failed: " + what);
        }
        void CheckEqual<T>(T expected, T actual, string what)
        {
            n++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(
                    $"OpenChamberBridge self-test failed: {what} (expected '{expected}', got '{actual}')");
        }

        // The id scheme. These fixtures are SYNTHETIC: the expected ids below were
        // recomputed from the paths beside them, not copied off a live machine.
        // That is not just privacy. An "observed id" fixture is only a real test if
        // someone else can verify it, and nobody outside the author's disk layout
        // could — recomputing makes every expected value independently checkable.
        CheckEqual("path_RDovUHJvamVjdHM", MakeId("D:\\Projects"),
                   "D:\\Projects projects to the expected id");
        CheckEqual("path_QzovVXNlcnMvZGVtbw", MakeId("C:\\Users\\demo"),
                   "C:\\Users\\demo projects to the expected id");
        CheckEqual("path_RjovZXhhbXBsZS9wZXJzb25hbCBjb250ZW50L2NvdXJzZS1ub3Rlcy90aGVzaXM",
                   MakeId("F:\\\u793a\u4f8b\\\u8d44\u6599\\\u8bba\u6587"),
                   "the CJK sample path projects to the expected id");

        // Decoding is the exact inverse, including CJK.
        CheckEqual("D:/Projects", DecodeId("path_RDovUHJvamVjdHM"), "decode D:/Projects");
        CheckEqual("C:/Users/demo", DecodeId("path_QzovVXNlcnMvZGVtbw"), "decode C:/Users/demo");
        CheckEqual("F:/\u793a\u4f8b/\u8d44\u6599/\u8bba\u6587",
                   DecodeId("path_RjovZXhhbXBsZS9wZXJzb25hbCBjb250ZW50L2NvdXJzZS1ub3Rlcy90aGVzaXM"),
                   "decode the CJK sample path");
        CheckEqual(MakeId("F:\\a\\b"), MakeId("F:/a/b/"), "separators and trailing slash normalise");
        CheckEqual(MakeId("F:\\a\\b"), MakeId("  F:\\a\\b  "), "surrounding whitespace is trimmed");

        // base64url alphabet: no '+' or '/' may survive, and no '=' padding.
        foreach (var p in new[] { "C:\\a+b", "C:\\a/b", "F:\\\u6587\u4ef6", "C:\\~~" })
        {
            var id = MakeId(p);
            Check(!id.Contains('+') && !id.Contains('/') && !id.Contains('='),
                  $"id for '{p}' is base64url without padding");
        }

        // Round trip on a CJK path with a trailing separator.
        var cjk = "F:\\\u793a\u4f8b\\\u8d44\u6599\\\u8bba\u6587";
        CheckEqual("F:/\u793a\u4f8b/\u8d44\u6599/\u8bba\u6587",
                   DecodeId(MakeId(cjk)), "CJK path round-trips through the id");

        // Rejections.
        Check(DecodeId(null) is null, "a null id decodes to null");
        Check(DecodeId("") is null, "an empty id decodes to null");
        Check(DecodeId("something-else") is null, "a foreign id decodes to null");
        Check(DecodeId("path_") is null, "a bare prefix decodes to null");
        Check(DecodeId("path_!!!!") is null, "an undecodable body decodes to null, not a throw");
        Check(Throws(() => MakeId("")), "an empty path is rejected");
        Check(Throws(() => MakeId("   ")), "a whitespace path is rejected");
        Check(Throws(() => MakeId("/")), "a lone separator is rejected");

        // Parsing the real settings shape.
        var doc = """
        {
          "sidebarSessionGroupingMode": "by-worktree",
          "activeProjectId": "path_QzovVXNlcnMvZGVtbw",
          "projects": [
            { "id": "path_A", "path": "F:\\a", "label": "A", "color": "comment",
              "addedAt": 1790597004438, "lastOpenedAt": 1790597004439,
              "sidebarCollapsed": false },
            { "id": "path_B", "path": "C:\\Users\\demo" }
          ]
        }
        """;
        var projects = ReadProjectsFrom(doc);
        CheckEqual(2, projects.Count, "both projects parse");
        CheckEqual("F:\\a", projects[0].Path, "path is read verbatim, backslashes intact");
        CheckEqual("A", projects[0].Label, "label is read");
        CheckEqual("comment", projects[0].Color, "color is read");
        CheckEqual(1790597004438L, projects[0].AddedAt, "addedAt is read");
        Check(!projects[0].SidebarCollapsed, "sidebarCollapsed is read");
        Check(projects[1].Label is null, "an absent label is null, not a throw");
        CheckEqual(0L, projects[1].AddedAt, "an absent addedAt is 0");

        // Junk must not throw: the file belongs to a live app.
        CheckEqual(0, ReadProjectsFrom("").Count, "empty input yields nothing");
        CheckEqual(0, ReadProjectsFrom("   ").Count, "whitespace input yields nothing");
        CheckEqual(0, ReadProjectsFrom("{ not json").Count, "malformed json yields nothing");
        CheckEqual(0, ReadProjectsFrom("{}").Count, "a document with no projects yields nothing");
        CheckEqual(0, ReadProjectsFrom("""{"projects": 5}""").Count, "a non-array projects yields nothing");
        CheckEqual(0, ReadProjectsFrom("""{"projects": [1, "two", null]}""").Count,
                   "non-object entries are skipped");
        CheckEqual(0, ReadProjectsFrom("""{"projects": [{"id":"x","path":123}]}""").Count,
                   "an entry with a non-string path is skipped");
        CheckEqual(0, ReadProjectsFrom("""{"projects": [{"id":456,"path":"C:\\\\a"}]}""").Count,
                   "an entry with a non-string id is skipped");
        CheckEqual(0, ReadProjectsFrom("""{"projects": [{"path":"C:\\\\a"}]}""").Count,
                   "an entry with no id is skipped");
        CheckEqual(1, ReadProjectsFrom("""{"projects": [{"id":"x","path":"C:\\\\a","sidebarCollapsed":"yes"}]}""").Count,
                   "a non-boolean sidebarCollapsed is not a throw");
        Check(!ReadProjectsFrom("""{"projects": [{"id":"x","path":"C:\\\\a","sidebarCollapsed":"yes"}]}""")[0].SidebarCollapsed,
              "a non-boolean sidebarCollapsed reads as false");
        CheckEqual(1, ReadProjectsFrom("""{"projects": [{"id":"x","path":"C:\\\\a","addedAt":"1790597004438"}]}""").Count,
                   "addedAt as a numeric string still parses");
        CheckEqual(1790597004438L, ReadProjectsFrom("""{"projects":[{"id":"x","path":"C:\\\\a","addedAt":"1790597004438"}]}""")[0].AddedAt,
                   "addedAt string value is converted");

        // LeafName.
        CheckEqual("paper", LeafName("F:\\a\\paper"), "leaf of a normal path");
        CheckEqual("paper", LeafName("F:\\a\\paper\\"), "trailing separator is ignored");
        CheckEqual("F:", LeafName("F:\\"), "a drive root falls back to the drive");
        CheckEqual("Users", LeafName("C:\\Users"), "leaf of a two-segment path");

        // A document that round-trips through activate's own shape.
        var activateShaped = JsonNode.Parse(doc)!.AsObject();
        activateShaped["activeProjectId"] = MakeId("F:\\a");
        activateShaped["projects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = MakeId("F:\\a"), ["path"] = "F:\\a", ["label"] = "a",
            ["addedAt"] = 1, ["lastOpenedAt"] = 2, ["sidebarCollapsed"] = false,
        });
        var reparsed = ReadProjectsFrom(activateShaped.ToJsonString());
        CheckEqual(3, reparsed.Count, "an appended project is seen on the next read");
        Check(reparsed.Any(p => p.Id == MakeId("F:\\a")), "the appended project is the one activated");

        // ---- restoring, which is what makes the backup worth taking ----
        //
        // VerifyRegistered can DETECT a write that went wrong. It cannot undo one:
        // it runs after the damage. Without a restore step the backup is a file
        // nobody reads, and "we would have thrown" is a report, not a repair.
        var rdir = Scratch("restore");
        try
        {
            var settings = Path.Combine(rdir, "settings.json");
            var pristine = doc;
            File.WriteAllText(settings, pristine, new UTF8Encoding(false));

            var saved = BackupSettings(settings, rdir);

            // Corrupt it the way a bad write would.
            File.WriteAllText(settings, """{"projects":[]}""", new UTF8Encoding(false));
            Check(CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8)) == 1,
                  "the damaged document really is damaged");

            var restored = RestoreSettings(saved, settings);
            Check(restored, "RestoreSettings reports that it put something back");
            CheckEqual(CountTopLevelKeys(pristine),
                       CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8)),
                       "the restored document has the original's key count");
            Check(pristine == File.ReadAllText(settings, Encoding.UTF8),
                  "and is byte-for-byte the original");

            // Restoring something that is not a settings document must be refused,
            // not copied over a live file.
            var junkBackup = Path.Combine(rdir, "settings-99999999-999999-999.json");
            File.WriteAllText(junkBackup, "{ not json", new UTF8Encoding(false));
            Check(!RestoreSettings(junkBackup, settings),
                  "a backup that does not parse is refused rather than restored");
            Check(pristine == File.ReadAllText(settings, Encoding.UTF8),
                  "and the live document is left alone");

            Check(!RestoreSettings(Path.Combine(rdir, "absent.json"), settings),
                  "a missing backup reports failure rather than throwing");
        }
        finally { Clear(rdir); }
        //
        // Everything below writes into a scratch directory. The real settings.json
        // belongs to a live Electron process, and a self-test that touched it would
        // be the exact failure this whole feature exists to avoid.

        static string Scratch(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "sl-bridge-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Clear(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a locked scratch file must not fail the suite */ }
        }

        // Catches the two exceptions these helpers throw for "the file is not what
        // you need it to be": a malformed document, and a missing one. Deliberately
        // not reusing the ArgumentException-only Throws below.
        static bool ThrowsDataProblem(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
            catch (FileNotFoundException) { return true; }
        }

        // IsRegistered is tested only through an explicit `known` list, so it never
        // reads the live settings file. Separator style is the case that matters:
        // OpenChamber writes forward slashes and the catalog carries both, and a
        // raw string compare misses almost every project — which is what already
        // happened once to the colour lookup.
        var known = ReadProjectsFrom(doc);
        Check(IsRegistered("F:/a", known), "a forward-slash path matches a backslash record");
        Check(IsRegistered("F:\\a", known), "and the reverse");
        Check(IsRegistered("F:\\A\\", known), "trailing separator and case do not matter");
        Check(!IsRegistered("F:/a/other", known), "a different folder is not registered");
        Check(!IsRegistered("", known), "an empty path is not registered");
        Check(!IsRegistered("   ", known), "a whitespace path is not registered");

        // Key counting, which is how a write proves it dropped nothing.
        CheckEqual(2, CountTopLevelKeys("""{"a":1,"b":2}"""), "two keys count as two");
        CheckEqual(1, CountTopLevelKeys("""{"a":1}"""), "one key counts as one");
        CheckEqual(0, CountTopLevelKeys("{ not json"), "unparseable json counts as zero");
        CheckEqual(0, CountTopLevelKeys(""), "empty input counts as zero");
        CheckEqual(0, CountTopLevelKeys("[]"), "an array root has no top-level keys");
        CheckEqual(3, CountTopLevelKeys(doc), "the fixture document's own key count");

        // Backup, on a scratch root.
        var bdir = Scratch("backup");
        try
        {
            var source = Path.Combine(bdir, "settings.json");
            File.WriteAllText(source, doc, new UTF8Encoding(false));

            var backup = BackupSettings(source, bdir);
            Check(File.Exists(backup), "BackupSettings writes a file that exists");
            CheckEqual(CountTopLevelKeys(doc),
                       CountTopLevelKeys(File.ReadAllText(backup, Encoding.UTF8)),
                       "the backup holds the same document");
            Check(backup.StartsWith(bdir, StringComparison.OrdinalIgnoreCase),
                  "the backup lands in the directory it was told to use");

            // Twelve more writes, so the directory is well past its bound and must prune:
            // the point of a backup directory is not defeated by itself.
            const int Writes = 12;
            // Collect the RETURNED names rather than counting files in the directory.
            // Pruning starts removing old copies as soon as the bound is passed, so
            // the directory cannot show whether a write was lost — but the returned
            // paths can. This assertion exists because it failed before: the name
            // used to be a millisecond stamp alone, so rapid writes collided on it,
            // overwrote each other, and a restore point silently disappeared.
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            returned.Add(BackupSettings(source, bdir));
            for (var i = 0; i < Writes; i++) returned.Add(BackupSettings(source, bdir));
            CheckEqual(Writes + 1, returned.Count,
                       "each write is given its own backup name, even within one millisecond");

            var kept = Directory.GetFiles(bdir, "settings-*.json");
            // The bound is BackupKeepCount PLUS the oldest copy, because pruning
            // never deletes the oldest — see the block below for why that matters.
            CheckEqual(BackupKeepCount + 1, kept.Length,
                       $"the backup directory prunes to {BackupKeepCount} plus the oldest");

            Check(ThrowsDataProblem(() => BackupSettings(Path.Combine(bdir, "nope.json"), bdir)),
                  "backing up a file that does not exist is rejected");

            // The oldest backup is the one that matters. Pruning newest-first past
            // it deletes the copy of the document as it was BEFORE this feature
            // touched it, which is the only copy that can undo every write at once.
            var oldest = Directory.GetFiles(bdir, "settings-*.json")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
            for (var i = 0; i < 20; i++) BackupSettings(source, bdir);
            Check(File.Exists(oldest),
                  "the oldest backup survives however many writes follow it");
            Check(Directory.GetFiles(bdir, "settings-*.json").Length <= BackupKeepCount + 1,
                  "and the directory still stays bounded");
        }
        finally { Clear(bdir); }

        // Activate writes the colour it was handed, so a project registered by the
        // launcher does not arrive in OpenChamber with no accent.
        var cdir = Scratch("color");
        try
        {
            var settings = Path.Combine(cdir, "settings.json");
            File.WriteAllText(settings, doc, new UTF8Encoding(false));

            // Activate refuses a directory that does not exist, which is deliberate:
            // registering a project the user cannot open helps nobody. So the fixture
            // uses a real scratch folder rather than a made-up path.
            var fresh = Path.Combine(cdir, "fresh");
            Directory.CreateDirectory(fresh);

            var id = Activate(fresh, "fresh", "error", settings);
            var after = ReadProjectsFrom(File.ReadAllText(settings, Encoding.UTF8));
            Check(after.Any(p => p.Id == id && p.Color == "error"),
                  "Activate records the colour it was given");
            CheckEqual(3, after.Count, "Activate added exactly one project");
            CheckEqual("fresh", after.Single(p => p.Id == id).Label,
                       "and gave it the label it was given");

            // An existing record updates its colour rather than duplicating itself.
            var again = Activate(fresh, "fresh", "primary", settings);
            CheckEqual(id, again, "re-activating resolves to the same id");
            CheckEqual(3, ReadProjectsFrom(File.ReadAllText(settings, Encoding.UTF8)).Count,
                       "re-activating does not add a duplicate");
            CheckEqual("primary",
                       ReadProjectsFrom(File.ReadAllText(settings, Encoding.UTF8))
                                  .Single(p => p.Id == id).Color,
                       "and updates the colour in place");
        }
        finally { Clear(cdir); }

        // VerifyRegistered is the gate the whole write path depends on, so each way
        // it can fail is asserted separately rather than through one catch.
        var vdir = Scratch("verify");
        try
        {
            var settings = Path.Combine(vdir, "settings.json");
            File.WriteAllText(settings, doc, new UTF8Encoding(false));
            var before = CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8));

            var target = Path.Combine(vdir, "fresh");
            Directory.CreateDirectory(target);

            Check(ThrowsDataProblem(() => VerifyRegistered(settings, MakeId(target), before)),
                  "an id that is not registered fails verification");
            Check(ThrowsDataProblem(() => VerifyRegistered(settings, "not-even-an-id", before)),
                  "a malformed id fails verification");

            var real = Activate(target, "fresh", "error", settings);
            var afterCount = CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8));
            Check(ThrowsDataProblem(() => VerifyRegistered(settings, real, afterCount + 1)),
                  "a document with fewer keys than the caller required is rejected");
            Check(!ThrowsDataProblem(() => VerifyRegistered(settings, real, before)),
                  "the pre-write key count is still accepted afterwards");

            Check(ThrowsDataProblem(() => VerifyRegistered(
                      Path.Combine(vdir, "gone.json"), real, before)),
                  "a settings file that vanished fails verification rather than passing");
        }
        finally { Clear(vdir); }

        // Last, because it is the one that must not throw: a real Activate followed
        // by a real verification on a real file.
        var edir = Scratch("endtoend");
        try
        {
            var settings = Path.Combine(edir, "settings.json");
            File.WriteAllText(settings, doc, new UTF8Encoding(false));
            var before = CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8));

            var fresh = Path.Combine(edir, "fresh");
            Directory.CreateDirectory(fresh);
            var id = Activate(fresh, "fresh", "error", settings);

            // Adding a project to the array must not LOSE a top-level key. It may gain
            // one: Activate sets sidebarProjectDisplayMode, which this fixture does
            // not carry, so the count legitimately rises by exactly that. A write
            // that replaced the document instead of editing it would show a count
            // far below `before`, which is what the floor is here to catch.
            var finalCount = CountTopLevelKeys(File.ReadAllText(settings, Encoding.UTF8));
            Check(finalCount >= before,
                  $"the write did not drop top-level keys ({before} -> {finalCount})");

            Check(!ThrowsDataProblem(() => VerifyRegistered(settings, id, before)),
                  "VerifyRegistered accepts a write Activate actually made");

            var root = JsonNode.Parse(File.ReadAllText(settings, Encoding.UTF8))!.AsObject();
            CheckEqual(id, root["activeProjectId"]!.GetValue<string>(),
                       "and activeProjectId names the project just activated");
            Check(root["projects"]!.AsArray().Any(
                      p => p!["id"]!.GetValue<string>() == id),
                  "and the project is in the array");
        }
        finally { Clear(edir); }

        return n;

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
    }
}
