# Created-time columns and project registration — design

Date: 2026-10-01
Status: awaiting review

## Problem

Two things are wanted, and they are unrelated except that they land in the same
release.

**A. Columns.** The user does not care about session ids, and does not care about
project colours. They want to see when a conversation was *created*, and when a
project was created. So:

- session view: drop `session id`, add `created`
- project view: drop `colour`, add `created`

The catalog table has no creation time today. `opencode.db`'s `session_v2` does
(`time_created`, Unix milliseconds), so the catalog generator has to start
emitting it.

**B. Opening a whole project.** Clicking a project should make OpenChamber's
sidebar show that project and every conversation under it — the tree in the
screenshot. Today it opens a single conversation via a deep link and the sidebar
does not change for most projects.

## What is actually true about B

Established by reading the OpenChamber source and by inspecting this machine's
live `settings.json`:

- `openchamber://` understands exactly three verbs: `connect`, `focus`, `session`.
  There is no project verb, and no deep link exists for projects.
- `packages/electron/entry.mjs:71` calls `app.requestSingleInstanceLock()`. A second
  instance exits immediately. A second OpenChamber is not available, and a separate
  `--user-data-dir` would not help because both instances would still share
  `~/.config/openchamber/settings.json`.
- `ipcMain.handle` exposes no project commands; the command set is window management.
- The deep-link handler `openSessionFromRoute` calls `setCurrentSession` and nothing
  else. It does not touch `useProjectsStore`.
- `addProject` is called from exactly one place: `DirectoryExplorerDialog`, which is
  the sidebar's `+` button. Nothing registers a project automatically.
- `main.mjs` has **no** `fs.watch` / `watchFile` on `settings.json`, and the renderer
  does not poll it. While OpenChamber runs, its in-memory state is authoritative and
  it overwrites the file on its next save.
- `main.mjs:2193` — closing the window calls `shouldHideMainWindowToTray`. The process
  survives. `window-all-closed` (line 5269) quits on Windows, but hiding to tray means
  it never fires. So a full quit requires the tray menu's **Quit**, which shows a
  confirmation dialog.
- On this machine `settings.json` already holds 4 registered projects: sample,
  Developer, `C:\Users\demo`, capstone. Our own list has 40 projects.

**Consequence.** For a project OpenChamber already knows, one deep link is enough —
OpenChamber switches `activeProjectId` by itself. For the other 36, the only ways to
register one are the sidebar `+` button, or writing `settings.json` while OpenChamber
is not running.

## Design

### A. Created-time columns

**Catalog format.** `TOP-LEVEL-SESSIONS.md` gains a `created` column:

```
| # | created | updated | msgs | agent | directory | title | session id |
```

Placed second, next to `updated`, because the two timestamps read together and
sorting is usually by one of them.

**Version tolerance is required, not optional.** Three parsers read this file and two
of them are outside this repo's app:

| Parser | Today | Change |
|---|---|---|
| `src/SessionLauncher.App/Services/SessionCatalog.cs` | `ExpectedHeader` compared cell-by-cell, indexes hardcoded to 7 columns | accept both 7- and 8-column headers; map indexes by header name |
| `src/SessionLauncher.Mcp/lib/catalog.mjs` | `cells[6] === 'session id'`, indexes hardcoded | same |
| `src/SessionLauncher.Mcp/refresh_catalog.mjs` | writes 7 columns | writes 8 |

Mapping by header name rather than position is the fix. The old layout is detected and
mapped to the same field names, so a stale catalog keeps working until it is
regenerated. Without this the app breaks on any catalog the user has not regenerated,
and the MCP server breaks with it.

**Model.** `SessionInfo` gains `DateTimeOffset Created`. `SessionRow` exposes
`CreatedText` formatted like `UpdatedText` (`yyyy-MM-dd HH:mm`, invariant).
`ProjectEntry` gains `Created` = the **earliest** session's `Created` in that project;
`ProjectRow` exposes `CreatedText`.

**XAML.**

- session: delete `ColId`, add `ColCreated` (width 132 at 100 %)
- project: delete the colour column, add a created column (width 150 at 100 %)

**Font scaling.** `ApplyColumnWidths` currently scales only the session columns. The
project columns are fixed pixels, so at 140 % the existing 最近使用 column already
clips and the new created column would too. The five project columns get `x:Name`s and
join `ApplyColumnWidths`. This is a small fix, included because otherwise the column
being added is broken at the font sizes the app already offers.

**Regeneration.** `refresh_catalog.mjs` regenerates `<catalog dir>\TOP-LEVEL-SESSIONS.md`.
That file is outside the repo and is shared, so it is backed up first to
`%LOCALAPPDATA%\SessionLauncher\backups\`.

### B. Opening a whole project

**A `● / ○` column** in the project list: registered in OpenChamber, or not. The state
is read from `settings.json` (`projects[].path`, compared through
`ProjectCatalog.Normalize`). Unregistered projects sort first, because they are the ones
that need attention. Read-only.

**On click of a project row:**

| Condition | Action | Writes settings.json |
|---|---|---|
| registered | one deep link to the project's newest conversation; OpenChamber switches project and shows its sessions | no |
| unregistered, OpenChamber not running | backup → atomic write → verify → launch | yes |
| unregistered, OpenChamber running | wait (see below) | only after it exits |

**Waiting.** Polls `Process.GetProcessesByName("OpenChamber")` every 700 ms until the
count is zero. A count of zero is the only reliable test: this machine has 4 processes
and only one owns a window, and closing that window only hides it to the tray.

- the status line names the exact action: quit from the **tray icon → Quit**, because
  closing the window is not enough and the user will otherwise wait forever
- a 取消等待 button cancels
- 5 minute timeout, then it stops and says so
- the window is never touched — no move, resize, topmost, minimise, or close

**The write.** Once OpenChamber is gone:

1. copy `settings.json` to `backups/`, keep the newest 10
2. parse, add `{id, path, label, color}` to `projects[]` if the path is absent, set
   `activeProjectId`
3. write to a temp file in the same directory, then replace
4. re-read and assert: the project is in `projects[]`, `activeProjectId` is its id, the
   JSON parses, and the key count did not decrease
5. launch OpenChamber

`OpenChamberBridge.Activate` already implements the id scheme and the atomic write and
is covered by tests; it is unreachable from the UI today and gets wired back up.

**Colour.** An unregistered project gets a colour from the same least-used palette the
project list already uses, so it does not arrive colourless in OpenChamber.

## Files

| File | Change |
|---|---|
| `src/SessionLauncher.Mcp/refresh_catalog.mjs` | select and emit `created` |
| `src/SessionLauncher.Mcp/lib/catalog.mjs` | header-name mapping, 7/8 columns |
| `Services/SessionCatalog.cs` | header-name mapping, 7/8 columns, `SessionInfo.Created` |
| `Models/SessionInfo.cs` | `Created` |
| `Models/ProjectRow.cs`, `Services/ProjectCatalog.cs` | `CreatedText` / `Created` |
| `MainWindow.xaml` | column swaps; `x:Name` on project columns |
| `MainWindow.xaml.cs` | `ApplyColumnWidths`; wait loop; wire `Activate` |
| `Services/OpenChamberBridge.cs` | `IsRegistered` lookup (read-only) |
| `Services/Loc.cs` | new strings |
| `<catalog dir>\TOP-LEVEL-SESSIONS.md` | regenerated, after backup |
| `README.md` | document both |

## Testing

- `run-selftest.ps1`: parser cases for 7- and 8-column rows, a header that must be
  rejected, a `created` value round-tripping to the right `DateTimeOffset`, and
  `ProjectEntry.Created` being the minimum of its sessions
- `ProjectCatalog` colour assignment unchanged; the new registration path reuses it
- live: regenerate the catalog, launch, confirm the two new columns render and the
  removed ones are gone, at 100 % and 140 % font
- live: click a registered project, confirm OpenChamber's sidebar switches and
  `activeProjectId` changes, and that `settings.json` is byte-identical before and after
- live: click an unregistered project, confirm the wait message names the tray Quit,
  cancel works, and the timeout fires
- live: quit OpenChamber, click again, confirm the write, the verification, and the
  relaunch with the project active

**Verification constraint.** UIA read-only. No `SetWindowPos`, no `ShowWindow`, no
topmost, no clicking OpenChamber. The screen-grab tool is not used on OpenChamber. A
previous round of damage came from window manipulation, and it is not repeated.

## Non-goals

- Opening several projects at once. `activeProjectId` is singular; one at a time.
- Opening conversations in tabs. `Header.tsx:737` renders the title row instead of
  `SessionTabsStrip` whenever a project label is showing, and `session-tabs-store` does
  not exist in local storage, so the tab-strip approach achieves nothing visible.
- Changing OpenChamber's own configuration format or source.
- Registering a project while OpenChamber is running.
