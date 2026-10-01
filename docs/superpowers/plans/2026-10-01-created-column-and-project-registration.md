# Created-time columns and project registration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show when each conversation and each project was created, and make clicking a project in the launcher make OpenChamber's sidebar show that project with all of its conversations — registering the project first when OpenChamber does not already know it.

**Architecture:** Two independent groups. Group A changes the catalog file's column layout and teaches both of its parsers to read by column *name* so the old 7-column layout keeps working. Group B reads OpenChamber's project registry, marks each project ●/○, and on click either sends one deep link (already registered) or waits for OpenChamber to exit, then registers the project and relaunches it.

**Tech Stack:** .NET 10 / WPF, no NuGet packages, BCL only. Node ≥ 22.5 for the MCP server (`node:sqlite`). PowerShell for the self-test harness.

**Spec:** `docs/superpowers/specs/2026-10-01-created-column-and-project-registration-design.md`

## Global Constraints

- **Never move, resize, topmost, minimise, or close the OpenChamber window.** No `SetWindowPos`, no `ShowWindow`, no synthetic clicks against OpenChamber. Reading its process list is allowed. This is non-negotiable — a previous round moved the user's window and the cause is recorded.
- Verify the UI with **UIA read-only** (AutomationElement property reads). `IsOffscreen` is unreliable in this environment — compare bounding rectangles against the window rect yourself instead.
- Synthetic mouse and keyboard events do **not** work against WPF here, including on a default-styled `ListView`. Do not conclude a row is unselectable from a failed synthetic click.
- The catalog file is shared with the MCP server. **Any change to its layout must land in `refresh_catalog.mjs`, `SessionCatalog.cs` and `lib/catalog.mjs` in the same release.** A parser that only understands one layout breaks the other reader.
- The self-test harness is `tools/run-selftest.ps1`, which copies a fixed `$sources` list into a throwaway console project. **A new service file that is not added to `$sources` is silently untested.** Add it in the same task that creates it.
- Build must stay at zero warnings (`TreatWarningsAsErrors` is on). Self-test assertions must all pass; the current count is 231 and only goes up.
- The new catalog column order is `| # | created | updated | msgs | agent | directory | title | session id |`.
- Timestamps render as `yyyy-MM-dd HH:mm`; a missing or unparseable one renders as `—` (em dash, U+2014), never as an empty cell.

## Review Focus

1. **A stale 7-column catalog is still on disk** because the user has not run the refresh yet, or the refresh failed. The app must list every session normally and the 创建时间 column must read `—` — not throw, not show zero sessions.
2. **A `created` cell is empty, whitespace, or non-numeric.** Treat it as "no value" and render `—`. A row that is short one column (a truncated write) must be skipped by the existing `skipped` counter, not crash on an index.
3. **The user closes OpenChamber's window instead of quitting it.** `main.mjs:2193` hides the window to the tray and the process survives, so the wait must keep polling — and the status text must say *tray icon → Quit*, because otherwise the user waits forever. The 5-minute timeout must then fire with a message that names the cause.
4. **The process count dips to zero transiently** — an updater restarting OpenChamber does exactly this. Write nothing on a single zero sample; require the count to be zero on **two consecutive polls** before declaring it exited.
5. **The user cancels the wait, switches view, or closes the launcher mid-poll.** No settings write happens, no orphaned timer survives, and the buttons return to enabled.

---

### Task 0: Version control before anything else

**Files:**
- Create: `.gitignore` at the repository root

**Interfaces:**
- Consumes: nothing.
- Produces: a working git repo at `<repo>\apps\SessionLauncher`, so every later task's "Commit" step is a real diff and a mistake is recoverable with `git checkout`.

- [ ] **Step 1: Initialise the repository and commit the current tree**

There is no VCS here. A source file was destroyed earlier in this project and had to be rebuilt by hand; that is what this step prevents recurring.

```powershell
cd <repo>\apps\SessionLauncher
git init
```

Create `.gitignore` excluding `bin/`, `obj/`, `.vs/`, `*.user`, and `artifacts/`, then:

```powershell
git add -A
git commit -m "chore: commit working tree before created-column work"
```

- [ ] **Step 2: Verify the commit landed**

Run: `git log --oneline` — Expected: one commit, and `git status --porcelain` is empty.

---

### Task 1: Catalog generator emits a `created` column

**Files:**
- Modify: `src/SessionLauncher.Mcp/refresh_catalog.mjs:17-18` (header comment), `:29-32` (`TABLE_HEADER`, `TABLE_SEPARATOR`), `:53` (JSDoc return type), `:60-68` (`SELECT`), `:99-104` (row emission), `:105-112` (sidecar object)
- Create: `tools/test-catalog.mjs`
- Modify: `package.json` (if one exists at the repo root — add a `"test:catalog"` script; skip this file if it does not)

**Interfaces:**
- Consumes: nothing.
- Produces: `TOP-LEVEL-SESSIONS.md` with header `| # | created | updated | msgs | agent | directory | title | session id |`, and `sessions.json` entries of shape `{id, title, directory, agent, created, updated, msgs}` where `created` and `updated` are epoch milliseconds.

- [ ] **Step 1: Write the failing test**

Create `tools/test-catalog.mjs` as a plain Node script — no test framework, matching the project's zero-dependency rule. It imports nothing from `refresh_catalog.mjs` (that module opens a database); instead it asserts the two exported header constants it does need by importing the module and reading `generate`'s output shape through a stubbed database path. Assert these, and throw with a message naming the assertion:

```
count of lines starting with '|' in the written markdown is rows + 3
the header line equals '| # | created | updated | msgs | agent | directory | title | session id |'
the separator line equals '| --- | --- | --- | --- | --- | --- | --- | --- |'
every row splits into exactly 8 cells
cell index 1 of a row parses as a Date (created) and differs from cell index 2 (updated) for at least one row
the sidecar's first entry has a numeric `created`
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/test-catalog.mjs` — Expected: FAIL, naming the header mismatch. The current header has 7 columns.

- [ ] **Step 3: Implement**

In `refresh_catalog.mjs`:

- `TABLE_HEADER` → `'| # | created | updated | msgs | agent | directory | title | session id |'`
- `TABLE_SEPARATOR` → `'| --- | --- | --- | --- | --- | --- | --- | --- |'` (eight cells)
- `SELECT`: add `s.time_created,` immediately after `s.time_updated,`
- The `@returns` JSDoc on `readSessions` gains `time_created:number`
- Row emission: compute `const created = formatLocal(row.time_created);` next to the existing `updated` and place `${created}` between the index and `${updated}`
- Sidecar: add `created: row.time_created,` before `updated: row.time_updated,`
- The header comment at `:17-18` — correct it to the 8-column format

- [ ] **Step 4: Run it to verify it passes**

Run: `node tools/test-catalog.mjs` — Expected: every assertion passes, exit 0.

- [ ] **Step 5: Commit**

```powershell
git add src/SessionLauncher.Mcp/refresh_catalog.mjs tools/test-catalog.mjs
git commit -m "feat(catalog): emit a created column"
```

---

### Task 2: `SessionCatalog` reads by column name, and sessions carry `Created`

**Files:**
- Modify: `src/SessionLauncher.App/Services/SessionCatalog.cs:15-18` (class remark), `:26-27` (`ExpectedHeader`), `:96-100` (header-not-found message), `:105-130` (row loop), `:141-153` (`IsHeaderRow`)
- Modify: `src/SessionLauncher.App/Models/SessionInfo.cs:30` (record body — add `Created`)

**Interfaces:**
- Consumes: the 8-column layout from Task 1.
- Produces:
  - `internal static IReadOnlyDictionary<string, int> BuildColumnMap(IReadOnlyList<string> headerCells)` — maps a lower-cased column name to its index; throws `InvalidDataException` when `#`, `updated`, `msgs`, `agent`, `directory`, `title` or `session id` is absent. `created` is optional.
  - `internal static IReadOnlyList<SessionInfo> LoadFromLines(IReadOnlyList<string> lines, string sourceName)` — the whole parse, with no file access. `Load()` becomes a thin wrapper that reads the file and calls it.
  - `SessionInfo.Created` — `public DateTimeOffset Created { get; init; } = DateTimeOffset.MinValue;`, an **init property in the record body, not a ninth positional parameter.** Every existing fixture constructs `SessionInfo` positionally; adding a positional member would silently break six of them in `ProjectCatalog.RunSelfTest` and one in `ProjectSort.RunSelfTest`.

- [ ] **Step 1: Write the failing test**

Add a new suite to `SelfTest.cs` (`SelfTest.cs` is a plain-logic suite in the same harness; it currently aggregates `ProjectCatalog`, `ProjectSort`, `WorkspaceService` and `OpenChamberBridge`). Add `internal static int RunSelfTest()` on `SessionCatalog` and call it from `SelfTest.Run()` alongside the others, plus add `'Services\SessionCatalog.cs'` to `$sources` in `tools/run-selftest.ps1`.

Assert exactly these, using two 4-line fixtures — a 7-column one and an 8-column one, both with the same three data rows:

```csharp
var seven = new[] {
    "# TOP-LEVEL-SESSIONS", "",
    "| # | updated | msgs | agent | directory | title | session id |",
    "| --- | --- | --- | --- | --- | --- | --- |",
    "| 1 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |",
    "| 2 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |",
};
var eight = new[] {
    "# TOP-LEVEL-SESSIONS", "",
    "| # | created | updated | msgs | agent | directory | title | session id |",
    "| --- | --- | --- | --- | --- | --- | --- | --- |",
    "| 1 | 2026-08-19 11:03 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |",
    "| 2 | 2026-07-02 08:00 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |",
};
```

- 7-column: 2 sessions; `ses_one` has the title `title one | with pipe` (the `\|` escape resolves); `ses_one.Updated` is 2026-10-01 17:39; `ses_one.Created == DateTimeOffset.MinValue`
- 8-column: 2 sessions; the same title; `ses_one.Created` is 2026-08-19 11:03 and `ses_two.Created` is 2026-07-02 08:00
- the 8-column `directory` cell keeps its backslashes: `ses_one.ProjectPath == @"F:\a\b"`
- a header that is neither layout → `InvalidDataException`
- a header missing `title` → `InvalidDataException`
- a header in **reversed** order (`| # | updated | msgs | agent | title | directory | session id |`) still parses, with `directory` and `title` not swapped — this is the whole point of name-based mapping
- a row with an empty `created` cell parses and yields `DateTimeOffset.MinValue`
- a row one cell short is counted as skipped, and the remaining rows still load

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL, `BuildColumnMap does not exist` or the 7-column fixture throwing, because `ExpectedHeader` still requires exactly 7 cells in a fixed order.

- [ ] **Step 3: Implement**

In `SessionCatalog.cs`:

- Delete `ExpectedHeader`. Add `private static readonly string[] RequiredColumns = { "#", "updated", "msgs", "agent", "directory", "title", "session id" };`
- `BuildColumnMap` lower-cases each cell, records `name → index` for every cell, then throws `InvalidDataException` naming the missing column if any of `RequiredColumns` is absent. Unknown extra columns are ignored, so a future column does not break it.
- Rewrite `IsHeaderRow` as a private `TryBuildColumnMap(string line, out IReadOnlyDictionary<string,int> map)` that returns false when the line is not a table row, and true when `BuildColumnMap` would succeed. The row loop then calls `Cell(cells, "title")` style lookups everywhere instead of `cells[5]`.
- Keep the `skipped` counter and the existing `id.Length == 0` skip.
- Keep `ParseTimestamp` and `ParseMessages` as they are; `created` goes through `ParseTimestamp`, which already returns `MinValue` on anything unparseable.
- Add `LoadFromLines(IReadOnlyList<string> lines, string sourceName)` and reduce `Load()` to file read plus that call.
- Fix the class remark at `:15-18`, which currently says "fixed 8-column header" while listing 7.

In `SessionInfo.cs`, add to the record body:

```csharp
/// <summary>When the conversation was first created, local time.</summary>
public DateTimeOffset Created { get; init; } = DateTimeOffset.MinValue;
```

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: `SELFTEST OK  assertions=<n>` with `n > 231`.

- [ ] **Step 5: Build**

Run: `dotnet build src\SessionLauncher.App\SessionLauncher.App.csproj -c Release` — Expected: zero errors, zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/SessionLauncher.App/Services/SessionCatalog.cs src/SessionLauncher.App/Models/SessionInfo.cs src/SessionLauncher.App/Services/SelfTest.cs tools/run-selftest.ps1
git commit -m "feat(catalog): read columns by name, accept 7 and 8 column layouts"
```

---

### Task 3: MCP parser accepts both layouts, then the shared catalog is regenerated

**Files:**
- Modify: `src/SessionLauncher.Mcp/lib/catalog.mjs:133-136` (`isHeader`), `:143-163` (`parseCatalog`), `:165-173` (`loadCatalog` JSDoc)
- Modify: `tools/test-catalog.mjs` (extend with parser cases)
- Regenerate: `<catalog dir>\TOP-LEVEL-SESSIONS.md` and `<catalog dir>\sessions.json`

**Interfaces:**
- Consumes: the 8-column layout from Task 1.
- Produces:
  - `export function columnMap(cells)` — returns an object mapping lower-cased column name to index, or `null` when a required column is missing. Required: `#`, `updated`, `msgs`, `agent`, `directory`, `title`, `session id`. `created` optional.
  - `parseCatalog(markdown)` returns entries of shape `{id, title, directory, agent, created, updated, msgs}` where `created` and `updated` are the **raw trimmed strings** (this parser never parsed dates — it passes `updated` through as text today, and adding a date type to one field and not the other would be a silent contract change for callers). `created` is `''` when the column is absent.
  - `isHeader` is removed. `escapeCell`, `splitRow`, `loadCatalog`, `resolveCatalogPath` keep their current signatures and export status.

- [ ] **Step 1: Write the failing test**

Extend `tools/test-catalog.mjs` with parser cases that call `parseCatalog` directly on inline markdown strings — no file, no database:

- the 7-column fixture yields 2 entries; entry 0 has `created === ''`, `id === 'ses_one'`, `title === 'title one | with pipe'`, `msgs === 1031`
- the 8-column fixture yields 2 entries; entry 0 has `created === '2026-08-19 11:03'`
- a reversed header parses with `directory` and `title` correctly assigned, not swapped
- a header missing `title` yields `[]`
- an entry whose `created` cell is empty yields `created === ''` and is not dropped
- a short row (fewer cells than the header) is skipped without throwing

- [ ] **Step 2: Run it to verify it fails**

Run: `node tools/test-catalog.mjs` — Expected: FAIL — the 8-column fixture yields `created` as the title text, because `parseCatalog` reads fixed indices 1/2/4/5/6.

- [ ] **Step 3: Implement**

In `lib/catalog.mjs`:

- Add `columnMap(cells)` as described. Lower-case each cell for the key; keep the raw array for value lookup.
- Rewrite `parseCatalog` to call `columnMap` once on the first line that yields a non-null map, then read every field through that map for the remainder. A table whose first candidate header is malformed is skipped; a table with no header at all still yields `[]` rather than throwing.
- Delete `isHeader`.
- Update the `loadCatalog` JSDoc shape to include `created`.
- Leave `splitRow`'s `\|` handling exactly as it is — the escape is what keeps a title containing a pipe inside one cell, and it already works.

- [ ] **Step 4: Run it to verify it passes**

Run: `node tools/test-catalog.mjs` — Expected: every assertion passes.

- [ ] **Step 5: Back up the shared catalog, then regenerate it**

The file lives outside the repo and is shared with anything else that reads it, so back it up before the first regeneration.

```powershell
$bk = "$env:LOCALAPPDATA\SessionLauncher\backups"
New-Item -ItemType Directory -Force -Path $bk | Out-Null
Copy-Item '<catalog dir>\TOP-LEVEL-SESSIONS.md' "$bk\TOP-LEVEL-SESSIONS.md.bak-7col"
cd <repo>\apps\SessionLauncher\src\SessionLauncher.Mcp
node refresh_catalog.mjs
```

Verify: the header line of `<catalog dir>\TOP-LEVEL-SESSIONS.md` now has 8 cells, and the row count is unchanged from the backup. If the row count dropped, restore the backup and stop — that means the query changed, which is a bug.

- [ ] **Step 6: Confirm the app still reads it**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes.

- [ ] **Step 7: Commit**

```powershell
git add src/SessionLauncher.Mcp/lib/catalog.mjs tools/test-catalog.mjs
git commit -m "feat(mcp): read catalog columns by name, accept 7 and 8 column layouts"
```

Note in the commit body that `<catalog dir>\TOP-LEVEL-SESSIONS.md` was regenerated, with the backup path.

---

### Task 4: 创建时间 columns in both lists

**Files:**
- Modify: `src/SessionLauncher.App/Services/ProjectCatalog.cs:78` (after `MessageCount`) — add `ProjectEntry.Created`
- Modify: `src/SessionLauncher.App/Models/ProjectRow.cs:49` — add `CreatedText`
- Modify: `src/SessionLauncher.App/MainWindow.xaml.cs:232-245` (`ApplyColumnWidths`), `:1197-1200` (adjacent to `SessionRow.UpdatedText`)
- Modify: `src/SessionLauncher.App/MainWindow.xaml:709-710` (delete the colour column), `:769-770` (replace `ColId` with `ColCreated`)
- Modify: `src/SessionLauncher.App/Services/Loc.cs:41` (`ColId`), `:134` (`ProjColColor`), `:178` (zh `col.id`), `:279` (zh `proj.colColor`), `:314` (en `col.id`), `:419` (en `proj.colColor`)

**Interfaces:**
- Consumes: `SessionInfo.Created` from Task 2.
- Produces:
  - `ProjectEntry.Created` — `public DateTimeOffset Created => Sessions.Count > 0 ? Sessions.Min(s => s.Created) : DateTimeOffset.MinValue;`. It is a computed expression, not a stored field, because the session list is the only source and the sessions change as the catalog is reloaded.
  - `ProjectRow.CreatedText` — `string`, `—` when the value is `MinValue`, otherwise `yyyy-MM-dd HH:mm` in invariant culture, matching `LastSeenText`'s format exactly.
  - `SessionRow.CreatedText` — `string`, same rule.
  - XAML column `ColCreated`, width 132 at 100 %.

- [ ] **Step 1: Write the failing test**

Extend `ProjectCatalog.RunSelfTest`'s existing fixtures rather than adding a new file — `ProjectEntry` lives here and the harness already builds real grouped projects. Set `Created` explicitly on the three `multi` sessions (the ones at `F:\x`) and assert:

```csharp
CheckEqual(t0, multi[0].Created, "Created is the earliest session's creation time");
```

Add one project whose sessions all carry `MinValue` and assert its `Created` is `MinValue`, so a stale 7-column catalog renders `—` rather than a 1601 date.

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL — `ProjectEntry.Created` does not exist.

- [ ] **Step 3: Implement the models**

`ProjectCatalog.cs`, after `MessageCount`:

```csharp
/// <summary>
/// When this project was created, i.e. the earliest creation time among its
/// sessions. A real minimum rather than Sessions[^1].Created, because the list
/// is ordered by Updated, not by Created.
/// </summary>
public DateTimeOffset Created =>
    Sessions.Count > 0 ? Sessions.Min(s => s.Created) : DateTimeOffset.MinValue;
```

`ProjectRow.cs`, beside `LastSeenText`:

```csharp
public string CreatedText => Entry.Created == DateTimeOffset.MinValue
    ? "—"
    : Entry.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
```

`MainWindow.xaml.cs`, beside `SessionRow.UpdatedText`:

```csharp
public string CreatedText =>
    Session.Created == DateTimeOffset.MinValue
        ? "—"
        : Session.Created.ToString("yyyy-MM-dd HH:mm");
```

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes.

- [ ] **Step 5: Swap the columns in the XAML**

- Delete `ColId` (`MainWindow.xaml:769-770`) and put in its place:
  `<GridViewColumn x:Name="ColCreated" Header="{markup:Tr col.created}" Width="132" DisplayMemberBinding="{Binding CreatedText}" />`
- Delete the colour column (`MainWindow.xaml:709-710`) and put in its place:
  `<GridViewColumn x:Name="PColCreated" Header="{markup:Tr proj.colCreated}" Width="150" DisplayMemberBinding="{Binding CreatedText}" />`
- **The folder icon in the name column keeps its colour binding.** `ProjectRow.ColorHex` and `ColorLabel` stay on the class — the user does not want a colour *column*, not the colour itself, and OpenChamber assigns a colour that is worth matching.

- [ ] **Step 6: Put the project columns under font scaling**

`ApplyColumnWidths` (`MainWindow.xaml.cs:232-245`) currently scales only the six session columns, so the project list clips at 140 % — including the column being added. Add `x:Name` to the five remaining project columns (`PColName`, `PColSessions`, `PColLast`, `PColPath`, plus the `PColCreated` from Step 5), then add to `ApplyColumnWidths`:

```csharp
Set(ColCreated, 132);
Set(PColName, 330);
Set(PColSessions, 92);
Set(PColCreated, 150);
Set(PColLast, 150);
Set(PColPath, 560);
```

and change `Set(ColId, 290);` to nothing — delete that line.

- [ ] **Step 7: Update the localised strings**

In `Loc.cs`: add `public const string ColCreated = "col.created";` and `public const string ProjColCreated = "proj.colCreated";`; delete `ColId` and `ProjColColor` with all four dictionary entries (zh and en). Add `[ColCreated] = "创建时间"` / `"Created"`, and `[ProjColCreated] = "创建时间"` / `"Created"`. Confirm nothing else references the two deleted keys — `grep -n "col\.id\|proj\.colColor" src\` must return nothing.

- [ ] **Step 8: Build and run the self-tests**

Run: `dotnet build src\SessionLauncher.App\SessionLauncher.App.csproj -c Release` then `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: zero warnings, all assertions pass.

- [ ] **Step 9: Commit**

```powershell
git add -A src/SessionLauncher.App
git commit -m "feat(ui): show creation time in place of session id and colour"
```

---

### Task 5: `OpenChamberBridge` registration helpers

**Files:**
- Modify: `src/SessionLauncher.App/Services/OpenChamberBridge.cs:210-289` (`Activate` — add the colour parameter), `:319-329` (`WriteSettings`), after `:317` (new members)
- Test: the existing `OpenChamberBridge.RunSelfTest` in the same file

**Interfaces:**
- Consumes: `ProjectCatalog.Normalize` (same assembly, no new dependency).
- Produces:
  - `public static bool IsRegistered(string projectPath, IReadOnlyList<OpenChamberProject>? known = null)` — compares through `ProjectCatalog.Normalize` on both sides, because OpenChamber stores forward slashes and the catalog carries both styles and this mismatch has already broken one lookup in this project.
  - `public static int CountTopLevelKeys(string json)` — the number of properties on the root object; `0` when the document will not parse.
  - `public static string BackupSettings(string settingsPath)` — copies the file to `%LOCALAPPDATA%\SessionLauncher\backups\settings-<yyyyMMdd-HHmmss-fff>.json`, prunes the directory to the newest 10, returns the backup's full path. Throws `IOException` if the source does not exist.
  - `public static string Activate(string projectPath, string? label = null, string? colorKey = null)` — one added optional parameter. When the record is created and `colorKey` is non-blank, write `["color"] = colorKey`, so a newly registered project does not arrive colourless in OpenChamber. The existing-record branch updates `color` the same way when `colorKey` is supplied.
  - `public static void VerifyRegistered(string settingsPath, string projectId, int minimumKeyCount)` — re-reads the file and throws `InvalidDataException` unless all four hold: the JSON parses, `projects[]` contains `projectId`, `activeProjectId == projectId`, and `CountTopLevelKeys` is at least `minimumKeyCount`.

- [ ] **Step 1: Write the failing test**

Extend `OpenChamberBridge.RunSelfTest`, which already builds fixture settings documents in memory. Add, using `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))` as a scratch directory and deleting it in a `finally`:

- `IsRegistered("F:/a/b", ...)` is true when the fixture project path is `F:\a\b`, and false for `F:/a/c`, and false for an empty path
- `IsRegistered` matches across separator styles with no `known` argument supplied (it reads the real settings file and must return, not throw, when there is none — so call it with an explicit `known` list in every assertion)
- `CountTopLevelKeys("""{"a":1,"b":2}""")` is 2; `CountTopLevelKeys("{ not json")` is 0; `CountTopLevelKeys("[]")` is 0
- `Activate` on a temp directory with `colorKey: "error"` produces a record with `"color": "error"` after a re-read
- `BackupSettings` returns an existing path, and the file at it parses to the same top-level key count as the original
- `BackupSettings` keeps only the newest 10: write 12 dummy backups into a scratch backup root first, then call it and assert the directory holds exactly 10 and that the oldest dummy is gone. If pruning by count makes this test order-dependent, assert instead that the just-written backup exists and that the total is `<= 10`.
- `VerifyRegistered` **throws** when `activeProjectId` names a different project, throws when the key count fell, throws when the file is not JSON, and does **not** throw after a real `Activate` on a temp directory

The last one is the end-to-end proof and must be written last, after the others pass.

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL — `IsRegistered` does not exist.

- [ ] **Step 3: Implement**

Add `IsRegistered`:

```csharp
public static bool IsRegistered(string projectPath, IReadOnlyList<OpenChamberProject>? known = null)
{
    if (string.IsNullOrWhiteSpace(projectPath)) return false;

    // ProjectCatalog, not a local normaliser: OpenChamber stores forward slashes
    // and the catalog carries both styles, and comparing raw strings is what made
    // a colour lookup miss on almost every project once already.
    var wanted = ProjectCatalog.Normalize(projectPath);
    if (wanted.Length == 0) return false;

    var projects = known ?? ReadProjects();
    return projects.Any(p => ProjectCatalog.Normalize(p.Path).Equals(wanted,
        StringComparison.OrdinalIgnoreCase));
}
```

Add `CountTopLevelKeys`, `BackupSettings` and `VerifyRegistered` as specified. `VerifyRegistered` reads the file fresh and evaluates all four conditions before throwing, so one call reports every failure rather than only the first.

`BackupSettings` prunes with `Directory.GetFiles(backupDir, "settings-*.json")` ordered by name descending (the timestamp format sorts lexicographically), keeping the first 10.

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes.

- [ ] **Step 5: Commit**

```powershell
git add src/SessionLauncher.App/Services/OpenChamberBridge.cs
git commit -m "feat(bridge): add registered lookup, backup, and write verification"
```

---

### Task 6: The wait state machine

**Files:**
- Create: `src/SessionLauncher.App/Services/ProjectRegistrationWait.cs`
- Modify: `tools/run-selftest.ps1:25-38` — add `'Services\ProjectRegistrationWait.cs'` to `$sources`
- Modify: `src/SessionLauncher.App/Services/LauncherService.cs` — add `IsOpenChamberRunning`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `LauncherService.IsOpenChamberRunning()` — `public static bool`, true when `Process.GetProcessesByName("OpenChamber").Length > 0`. Zero is the only reliable test: this machine has four such processes and only one owns a window, and closing that window hides it to the tray rather than exiting.
  - `ProjectRegistrationWait` — `public sealed class ProjectRegistrationWait : IDisposable`, constructed as `ProjectRegistrationWait(TimeSpan pollInterval, TimeSpan timeout, Func<int>? processCount = null)`. `processCount` defaults to `LauncherService.IsOpenChamberRunning` expressed as a count, and the tests inject their own delegate so nothing real is polled.
    - `public bool IsWaiting { get; }`
    - `public Task<bool> WaitForExitAsync(CancellationToken ct)` — resolves `true` only when the count has been zero on **two consecutive polls**, `false` on cancel or on timeout.
    - `public void Cancel()`
    - `public void Dispose()` — cancels an in-flight wait and disposes the internal `CancellationTokenSource`.
  - `public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(700);` and `public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);`

- [ ] **Step 1: Write the failing test**

Give `ProjectRegistrationWait` its own `public static int RunSelfTest()` and add `'Services\ProjectRegistrationWait.cs'` to `$sources`. Assert:

- a delegate returning `4, 4, 0, 0` resolves `true`, and the zero run must be **two** samples: a delegate returning `4, 0, 4, 4, 0, 0` also resolves `true` and the transient `0` at index 1 did not resolve it — assert this by having the delegate record how many times it was called before the result came back
- a delegate that returns `0` once and then `4` forever, with a 300 ms timeout, resolves `false` (Review Focus #4)
- a delegate that returns `4` forever with a 300 ms timeout resolves `false`
- `Cancel()` while waiting resolves `false`
- passing an already-cancelled token resolves `false` without polling at all (the delegate throws if called)
- `IsWaiting` is false before the call, true while waiting, false after
- disposing mid-wait does not throw and leaves `IsWaiting` false
- `new ProjectRegistrationWait(TimeSpan.Zero, TimeSpan.FromSeconds(30), () => 0).WaitForExitAsync(default).GetAwaiter().GetResult()` is `true` — the interval is injectable, so the suite does not actually sleep

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL — the file does not exist yet.

- [ ] **Step 3: Implement**

`ProjectRegistrationWait.cs`, BCL only, no WPF, `#nullable enable` to match `ProjectSort.cs`:

```csharp
public sealed class ProjectRegistrationWait : IDisposable
{
    public ProjectRegistrationWait(TimeSpan pollInterval, TimeSpan timeout,
                                   Func<int>? processCount = null)
    public bool IsWaiting { get; }
    public Task<bool> WaitForExitAsync(CancellationToken ct)
    public void Cancel()
    public void Dispose()
}
```

The body is a `Task.Run` loop over `Task.Delay(pollInterval, ct)`, tracking `consecutiveZeros` and returning `true` the moment it reaches 2. Wrap the delay loop in a `try/catch (OperationCanceledException)` that returns `false`, and a `Stopwatch`-based deadline check that returns `false` on timeout. `Dispose` cancels the internal `CancellationTokenSource` and disposes it; make the class safe to dispose twice.

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes, and the suite finishes in well under its previous runtime.

- [ ] **Step 5: Commit**

```powershell
git add src/SessionLauncher.App/Services/ProjectRegistrationWait.cs tools/run-selftest.ps1 src/SessionLauncher.App/Services/LauncherService.cs
git commit -m "feat: add the OpenChamber exit wait state machine"
```

---

### Task 7: Registration marks and the ●/○ column

**Files:**
- Modify: `src/SessionLauncher.App/Services/ProjectSort.cs:25-59` (`ProjectSortMode`), `:65-123` (`Apply`), `:130-150` (`Toggle`)
- Modify: `src/SessionLauncher.App/Models/ProjectRow.cs:59` — add `IsRegistered` and `MarkText`
- Modify: `src/SessionLauncher.App/MainWindow.xaml.cs:753-764` (`ProjectSortModes`), `:887-909` (`ReloadProjects`), `:912-948` (`ApplyProjectFilter`)
- Modify: `src/SessionLauncher.App/MainWindow.xaml:680` — add the mark column
- Modify: `src/SessionLauncher.App/Services/Loc.cs` — add `ProjColMark`, `ProjSortUnregisteredFirst`

**Interfaces:**
- Consumes: `OpenChamberBridge.IsRegistered` from Task 5.
- Produces:
  - `ProjectSortMode.UnregisteredFirst` — added as the **last** enum member, after `PathDepth`. Position is load-bearing: `LastUsed` must stay at ordinal 0 because `AppSettings.ProjectSort` defaults to it and any saved integer keeps meaning the same mode.
  - `ProjectSort.Apply(IEnumerable<ProjectEntry> projects, ProjectSortMode mode, IReadOnlySet<string>? registeredPaths = null)` — the third parameter is optional, so the existing self-test loop over every enum value compiles and runs unchanged. Only `UnregisteredFirst` reads it.
  - `ProjectRow.IsRegistered` — `public bool IsRegistered { get; init; }`, set in `ReloadProjects`.
  - `ProjectRow.MarkText` — `public string MarkText => IsRegistered ? "●" : "○";`
  - XAML column `PColMark`, width 46 at 100 %.

- [ ] **Step 1: Write the failing test**

Extend `ProjectSort.RunSelfTest`. The existing `P(...)` fixture helper builds a `ProjectEntry` from a path and a time band; reuse it, and add:

- `UnregisteredFirst` with `registeredPaths` containing `Y:\alpha` puts `X:\beta` (unregistered) first
- `UnregisteredFirst` with `registeredPaths` null leaves the order identical to `LastUsed` — no registration data means no reordering
- `UnregisteredFirst` places **every** unregistered project ahead of **every** registered one, even when a registered project is far more recently used: assert that with 7 fixtures and 3 registered, the first 4 results are all unregistered
- `Toggle(UnregisteredFirst) == UnregisteredFirst`
- `Enum.GetValues<ProjectSortMode>()[0] == ProjectSortMode.LastUsed` — pins the "new member goes last" rule
- the existing per-mode loop still passes for the new mode: order-independent of input, and no rows dropped or duplicated

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL — `UnregisteredFirst` is not a member.

- [ ] **Step 3: Implement the sort mode**

Add the enum member after `PathDepth`, with a doc comment explaining that registration is a first-level grouping and the recency ordering is the tiebreak. Add to `Apply`:

```csharp
ProjectSortMode.UnregisteredFirst => projects
    .OrderBy(p => registeredPaths is not null
                 && registeredPaths.Contains(ProjectCatalog.Normalize(p.Path)) ? 1 : 0)
    .ThenByDescending(p => p.LastUsed)
    .ThenBy(p => p.Path, byPath),
```

Add `ProjectSortMode.UnregisteredFirst => ProjectSortMode.UnregisteredFirst,` to `Toggle` — it has no directional sense, like `PathDepth`.

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes.

- [ ] **Step 5: Mark the rows**

`ProjectRow.cs`:

```csharp
/// <summary>True when OpenChamber already lists this project.</summary>
public bool IsRegistered { get; init; }

/// <summary>Filled when registered, hollow when not.</summary>
public string MarkText => IsRegistered ? "●" : "○";
```

`ReloadProjects` (`MainWindow.xaml.cs:887`) — it already reads OpenChamber's projects for colours. Extend the same `try` to build a normalised path set, and pass each row's flag in the projection:

```csharp
_projects = ProjectCatalog.Group(_all, colors)
                         .Select(p => new ProjectRow(p) { IsRegistered = registered.Contains(p.Path) })
                         .ToList();
```

where `registered` is a `HashSet<string>(StringComparer.OrdinalIgnoreCase)` of `ProjectCatalog.Normalize(path)` values. The existing `catch` already swallows `IOException`, `JsonException` and `UnauthorizedAccessException` and sets `colors = null` — set `registered` to an empty set in that same branch, so an unreadable settings file degrades to "everything unregistered" rather than throwing during load.

`ApplyProjectFilter` (`MainWindow.xaml.cs:912`) — build the set from the current rows and pass it through:

```csharp
var registered = rows.ToHashSet(p => ProjectCatalog.Normalize(p.Path), StringComparer.OrdinalIgnoreCase);
var ordered = ProjectSort.Apply(rows.Select(p => p.Entry), sort, registered);
```

- [ ] **Step 6: Add the mark column and the sort option**

`MainWindow.xaml` — insert before the existing name column at `:680`:

```xml
<GridViewColumn x:Name="PColMark" Header="{markup:Tr proj.colMark}" Width="46"
                DisplayMemberBinding="{Binding MarkText}" />
```

`ApplyColumnWidths` — add `Set(PColMark, 46);`.

`ProjectSortModes` (`MainWindow.xaml.cs:753`) — put the new mode **first** in the array so it is the combo's top entry:

```csharp
(ProjectSortMode.UnregisteredFirst, Loc.ProjSortUnregisteredFirst),
```

Leave `AppSettings.ProjectSort`'s default at `LastUsed`. A saved choice stays untouched, and the new option is one click away.

`Loc.cs` — add `ProjColMark = "proj.colMark"` and `ProjSortUnregisteredFirst = "proj.sortUnregisteredFirst"`, with zh `"已注册优先"` → actually `"未注册在前"` / en `"Unregistered first"`, and `[ProjColMark] = "OpenChamber"` / `"OpenChamber"`.

- [ ] **Step 7: Build and test**

Run: `dotnet build src\SessionLauncher.App\SessionLauncher.App.csproj -c Release` then `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: zero warnings, all assertions pass.

- [ ] **Step 8: Commit**

```powershell
git add -A src/SessionLauncher.App
git commit -m "feat(ui): mark registered projects and add an unregistered-first sort"
```

---

### Task 8: The dispatch, the wait, and the cancel button

**Files:**
- Modify: `src/SessionLauncher.App/MainWindow.xaml.cs` — fields near `:68`, `OpenProjectSet` at `:1129`, `OnProjectOpenSetClick` at `:1116`, `OnProjectDoubleClick` at `:1038`, `OnWindowClosed` (new handler)
- Modify: `src/SessionLauncher.App/MainWindow.xaml:594-596` — the status row
- Modify: `src/SessionLauncher.App/Services/Loc.cs` — add the four new keys

**Interfaces:**
- Consumes: `OpenChamberBridge.IsRegistered` / `BackupSettings` / `Activate` / `VerifyRegistered` (Task 5), `LauncherService.IsOpenChamberRunning` and `ProjectRegistrationWait` (Task 6), `ProjectRow.IsRegistered` (Task 7).
- Produces:
  - `MainWindow.OpenProjectSet(ProjectRow project)` — the single dispatcher for both the button and the double-click. Both call sites already route here, so neither needs changing beyond confirming that.
  - `MainWindow.RegisterAndLaunch(ProjectRow project)` — `private void`.
  - `MainWindow.OnCancelRegistrationWaitClick(object sender, RoutedEventArgs e)` — cancels and restores the action row.
  - XAML: the status row becomes a `StackPanel` holding `StatusText` and a `CancelWaitButton` (default `Visibility="Collapsed"`).

- [ ] **Step 1: Write the failing test**

This is the one task whose logic is nearly all UI. Test the parts that are not:

Add to `OpenChamberBridge.RunSelfTest` an end-to-end shape test that stands in for the dispatcher's write: given a settings document with 58 top-level keys and four projects, call `Activate` on a temp directory, then `VerifyRegistered`, and assert the key count after the write is still exactly 58 — the number the Global Constraints name. Add a fourth project that was not there before and assert the count is still 58 (adding a project to the array does not add a root key) — this is the assertion that catches a write that replaces the document instead of editing it.

The dispatch decision itself is a pure function and deserves one:

```csharp
// in MainWindow's own terms, but pure and testable:
public enum RegistrationAction { OpenByDeepLink, RegisterThenLaunch, WaitForExit }
public static RegistrationAction Decide(bool isRegistered, bool openChamberRunning)
```

with `Decide(true, _) => OpenByDeepLink`, `Decide(false, false) => RegisterThenLaunch`, `Decide(false, true) => WaitForExit`. Put it on `ProjectRegistrationWait` as `public static RegistrationAction Decide(...)`, add the enum there, and add it to `$sources`. Assert all three cases.

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: FAIL — `Decide` does not exist.

- [ ] **Step 3: Implement the decision function**

In `ProjectRegistrationWait.cs`:

```csharp
/// <summary>What clicking a project row should do.</summary>
public enum RegistrationAction { OpenByDeepLink, RegisterThenLaunch, WaitForExit }

/// <summary>
/// Pick the action. Registered needs nothing written, so it is a deep link no
/// matter what. Unregistered needs the settings write, which is only safe once
/// OpenChamber is gone, hence the third state.
/// </summary>
public static RegistrationAction Decide(bool isRegistered, bool openChamberRunning)
    => isRegistered ? RegistrationAction.OpenByDeepLink
       : openChamberRunning ? RegistrationAction.WaitForExit
       : RegistrationAction.RegisterThenLaunch;
```

- [ ] **Step 4: Run it to verify it passes**

Run: `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: passes.

- [ ] **Step 5: Rework `OpenProjectSet` into the dispatcher**

Replace the body of `OpenProjectSet(ProjectRow project)` at `MainWindow.xaml.cs:1129` with a switch on `ProjectRegistrationWait.Decide(project.IsRegistered, LauncherService.IsOpenChamberRunning())`:

- `OpenByDeepLink` — the existing body, unchanged: `_launcher.OpenSessionSetInOpenChamber(project.SessionIds)` and the `OcOpenedSet` status. Zero writes.
- `RegisterThenLaunch` — call `RegisterAndLaunch(project)`.
- `WaitForExit` — set the status to `Loc.Format(Loc.OcWaitingQuit, project.Name)`, reveal `CancelWaitButton`, disable `ProjectOcButton`, start the wait, and `await` it from an `async void` continuation that dispatches through `Dispatcher.InvokeAsync` because the await resumes on a pool thread.

The empty-project guard at the top of `OpenProjectSet` stays.

- [ ] **Step 6: Implement `RegisterAndLaunch`**

```csharp
private void RegisterAndLaunch(ProjectRow project)
{
    var settings = OpenChamberBridge.ResolveSettingsPath()
        ?? throw new FileNotFoundException(
            "OpenChamber settings.json was not found; is OpenChamber installed?");

    // Count BEFORE the write, so VerifyRegistered can prove nothing was dropped.
    var before = OpenChamberBridge.CountTopLevelKeys(
        File.ReadAllText(settings, Encoding.UTF8));

    OpenChamberBridge.BackupSettings(settings);

    var id = OpenChamberBridge.Activate(project.Path, project.Name, project.Entry.ColorKey);
    OpenChamberBridge.VerifyRegistered(settings, id, before);

    _launcher.OpenChamberApp();
    StatusText.Text = Loc.Format(Loc.OcRegisteredSet, project.Name, project.SessionCount);
}
```

Wrap the whole thing in the existing `Guard` so a failure surfaces in the status line rather than as an unhandled exception.

- [ ] **Step 7: The status row and the cancel button**

`MainWindow.xaml:594-596` — the status `TextBlock` is docked `Bottom` on its own. Replace it with a horizontal `StackPanel` that is `DockPanel.Dock="Bottom"`, containing `StatusText` (same margin, `TextTrimming` preserved) and `CancelWaitButton`:

```xml
<StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="2,8,0,0">
    <TextBlock x:Name="StatusText" Style="{StaticResource Muted}"
               TextTrimming="CharacterEllipsis" />
    <Button x:Name="CancelWaitButton" Style="{StaticResource IconButton}"
            Content="{markup:Tr act.cancelWait}" Margin="10,0,0,0"
            Visibility="Collapsed" Click="OnCancelRegistrationWaitClick" />
</StackPanel>
```

The button lives in the status row, **not** in `ProjectOpenRow`, because `ApplyView` collapses `ProjectOpenRow` when the user switches to the session view — and a wait that becomes uncancellable because the user glanced at another tab is a bug (Review Focus #5).

- [ ] **Step 8: Handle cancel, close, and restore**

`OnCancelRegistrationWaitClick` — cancel the wait, set the status to `Loc.T(Loc.OcWaitCancelled)`, hide `CancelWaitButton`, re-enable `ProjectOcButton`.

Add a `Closed` handler on the window (`MainWindow.xaml.cs` already opens windows without an `Owner`; add `Closed="OnWindowClosed"` to the `Window` element in the XAML) that disposes `_wait` so no timer outlives the app.

Every exit path from the wait — success, cancel, timeout — must go through one helper that hides `CancelWaitButton` and re-enables `ProjectOcButton`. Write that helper as `private void EndRegistrationWait()` and call it from all three.

- [ ] **Step 9: Add the strings**

`Loc.cs` — add four keys with zh and en values:

- `OcWaitingQuit` — zh: `「{0}」未在 OpenChamber 中注册。请从系统托盘图标右键 → Quit 完全退出 OpenChamber（直接关窗口只是隐藏到托盘，进程不会退出）。退出后我会自动完成注册并重新打开。` / en: `'"{0}" is not registered in OpenChamber. Quit it from the system tray icon (right-click → Quit) — closing the window only hides it to the tray and the process stays alive. I will register it and reopen OpenChamber as soon as it exits.'`
- `OcWaitCancelled` — zh: `已取消等待。` / en: `Cancelled waiting.`
- `OcWaitTimedOut` — zh: `等待超时：5 分钟内 OpenChamber 没有退出。请从托盘右键 → Quit 完全退出后再点一次。` / en: `Timed out: OpenChamber did not exit within 5 minutes. Quit it from the tray (right-click → Quit) and try again.`
- `OcRegisteredSet` — zh: `已注册「{0}」并在 OpenChamber 中打开其 {1} 条会话。` / en: `Registered "{0}" and opened its {1} session(s) in OpenChamber.`
- `act.cancelWait` — zh: `取消等待` / en: `Cancel wait`

- [ ] **Step 10: Build and test**

Run: `dotnet build src\SessionLauncher.App\SessionLauncher.App.csproj -c Release` then `powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1` — Expected: zero warnings, all assertions pass.

- [ ] **Step 11: Commit**

```powershell
git add -A src/SessionLauncher.App
git commit -m "feat: register unregistered projects after waiting for OpenChamber to exit"
```

---

### Task 9: Live acceptance

**Files:** none modified unless a defect is found.

**Interfaces:**
- Consumes: everything above.
- Produces: a written record of what was observed, appended to `docs/superpowers/specs/2026-10-01-created-column-and-project-registration-design.md` under a `## Verification log` heading.

**Verification constraint, restated because it is the one that has already gone wrong once:** read OpenChamber's state through UIA or through `settings.json`. Do not move, resize, topmost, minimise or close its window; do not click it; do not run `tools/shot-screen.ps1` against it.

- [ ] **Step 1: Launch and read the two new columns**

Start the app, switch to the session view, and via UIA read the column headers of `List`. Assert 创建时间 is present and 会话 ID is absent. Switch to the project view and assert the same for 创建时间 against 颜色.

- [ ] **Step 2: Check the scaling**

Set the font to 140 % with the existing in-app buttons. Read the 创建时间 column's rendered text via UIA for three rows and assert none is truncated to `2026-10-01 1`. Reset to 100 %.

- [ ] **Step 3: Check the marks**

In the project view, count the `●` and `○` rows. The four projects OpenChamber currently holds — sample, Developer, `C:\Users\demo`, capstone — must be `●`. Every other row must be `○`.

- [ ] **Step 4: Check the registered path writes nothing**

Hash `C:\Users\demo\.config\openchamber\settings.json`. Select the `●`-marked sample row, click the open-set button, wait, then re-hash. Assert the hash is **unchanged**. Read `activeProjectId` before and after and assert it changed to sample's id — that is OpenChamber switching projects on its own in response to the deep link, which is the whole mechanism.

- [ ] **Step 5: Check the wait and the cancel**

Select an `○` row while OpenChamber is running. Assert the status text appears and names the tray Quit. Click 取消等待 and assert the button hides, `ProjectOcButton` re-enables, and the settings hash is still unchanged.

- [ ] **Step 6: Check the timeout without waiting five minutes**

Temporarily set `DefaultTimeout` to `TimeSpan.FromSeconds(3)`, rebuild, and repeat Step 5 but let it run out. Assert the timeout message appears, the button hides, and the hash is unchanged. **Restore the 5-minute default and rebuild before committing.**

- [ ] **Step 7: Check the full write path**

Quit OpenChamber from the tray yourself — the app must not do it. Then select the same `○` row. Assert the status reports registration, OpenChamber starts, a backup file appeared under `%LOCALAPPDATA%\SessionLauncher\backups\`, and OpenChamber's sidebar shows the project node with its conversations beneath it. Read `settings.json` and assert the project is in `projects[]`, `activeProjectId` names it, and the top-level key count is not lower than before.

- [ ] **Step 8: Record the outcome**

Append what you observed, with timestamps, to the spec's `## Verification log` section. Commit any fix made along the way; if you found no defect, commit only the log.

```powershell
git add docs/superpowers/specs/2026-10-01-created-column-and-project-registration-design.md
git commit -m "docs: record live verification of created columns and project registration"
```

---

## Self-Review

**Spec coverage.** Every section of the spec maps to a task: the catalog column layout → 1, 2, 3; name-based parsing for all three readers → 2, 3; `SessionInfo.Created` → 2; `ProjectEntry.Created` as the earliest session → 4; the two column swaps → 4; font scaling for the project columns → 4; the ●/○ column and unregistered-first ordering → 7; deep link for registered → 8; backup, atomic write, verification, relaunch for unregistered → 5, 8; the wait with its 700 ms poll, tray-Quit wording, cancel and 5-minute timeout → 6, 8; the colour assigned on registration → 5; the regeneration with a backup → 3. Non-goals are untouched.

**Two deliberate deviations from the spec, both flagged for the user's call.** The spec said unregistered projects "sort first". Rather than silently overriding whichever of the nine sort modes the user has chosen, Task 7 adds it as a tenth mode at the top of the combo and leaves `AppSettings.ProjectSort`'s default alone. Second, the spec's unregistration check was a single zero process count; Review Focus item 4 requires two consecutive zeros, because an updater restart produces a single dip.

**Step scan.** Every step names a file, a signature or exact values, and the command that proves it. The bodies of `Group`, `Normalize`, `ParseTimestamp`, `Activate`'s record-merge, `splitRow` and `WriteSettings` are existing code with existing tests and are referenced, not re-described.

**Type consistency.** `SessionInfo.Created` is an init property and is set by name at every construction site, so the eight positional arguments in the existing fixtures stay valid. `ProjectSort.Apply` gains a third optional parameter, so its existing two-argument self-test loop compiles unchanged. `ProjectRegistrationWait` is referenced with the same constructor shape in Task 6's tests and Task 8's caller. `RegistrationAction` is declared in Task 6's file in Step 3 and referenced by the same name in Task 8's Step 5.

**Review Focus coverage.** Item 1 is asserted in Task 2's 7-column fixture. Item 2 is asserted by the empty-`created` and short-row cases in Task 2. Item 3 is Task 9 Step 6 plus the tray wording in Task 8 Step 9. Item 4 is the two-consecutive-zeros assertion in Task 6. Item 5 is Task 6's cancel and dispose assertions plus the cancel button's placement in Task 8 Step 7.

**Proportion.** Nine tasks, roughly one screen each, no task restates the spec.