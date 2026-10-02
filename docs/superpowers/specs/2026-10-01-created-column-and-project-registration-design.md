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

- the status line states what will happen and says explicitly that **nothing is required
  of the user**. It keeps one factual note — that closing the window only hides to the
  tray, so a real exit is the tray's **Quit** — because without it a user who closes the
  window sees a feature that looks broken rather than idle
- a 取消等待 button cancels
- **there is no timeout.** The user quits OpenChamber whenever they get round to it,
  possibly hours later, and registration happens by itself afterwards. A deadline would
  expire while they were away and oblige them to come back and press the button a second
  time, which is the "go and do something now" this path exists to avoid
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

## Verification log

2026-10-01. All checks read state through UIA on our own window, or read
`settings.json` as a file. OpenChamber's window was never moved, resized, topmosted,
minimised, closed or clicked; its four processes were only ever counted.

| # | Check | Result |
|---|---|---|
| 1 | Catalog regenerated from `opencode.db` | 141 → 157 sessions, **0 lost**; header 8 columns; 0 rows whose column count is not 8; 0 empty `created` cells; 0 duplicate ids |
| 2 | App reads the new catalog | status line reports **157 条会话** |
| 3 | 会话 view columns | 选 · 更新时间 · 消息数 · 标题 · 文件夹 · **创建时间**; **会话 ID absent** |
| 4 | 项目 view columns | OpenChamber · 项目 · 会话 · **创建时间** · 最近使用 · 路径; **颜色 absent** |
| 5 | Registration marks | **4 filled, 21 hollow** — matching the four projects in the live `settings.json` exactly |
| 6 | Font scaling | at **160 %** (above the 140 % target): 82 complete timestamps, **0 truncated** |
| 7 | Registered project → deep link | `activeProjectId` moved on its own from `path_QzovVXNlcnMvZGVtbw` to capstone's `path_RjovZXhhbXBsZS…`; top-level keys 58 → 58; projects 4 → 4; project set unchanged → **the launcher wrote nothing** |
| 8 | Unregistered project, OpenChamber running | tray-Quit instruction shown; 取消等待 button revealed; `settings.json` **byte-identical** |
| 9 | Cancel | 已取消等待。 ; `settings.json` still byte-identical; button hidden |
| 10 | Timeout | message shown; `settings.json` still byte-identical; button hidden |
| 11 | Second click during a wait | the wait survives; the second press is refused with 已在等待…; shape unchanged |

Steps 1–11 are automated and repeatable via `tools\test-catalog.mjs` (47 assertions),
`tools\run-selftest.ps1` (344 assertions), `tools\verify-columns.ps1`,
`tools\verify-scaling.ps1`, `tools\verify-deep-link.ps1` and
`tools\verify-registration.ps1`.

A whole-branch review afterwards found one Critical and three Important problems, all
since fixed; the dispositions are in
`.superpowers/sdd/2026-10-01-created-column-and-project-registration/progress.md` under
`## Final review`. The two that changed observable behaviour most:

- `WriteSettings` was doing `File.Delete` then `File.Move` under a comment claiming the
  write was atomic on NTFS. Between those lines the user's entire OpenChamber
  configuration did not exist. It predated this work, but this work made it reachable
  from a button, so it is now `File.Replace`.
- A second click during a wait disposed the live wait, and the disposed one's
  continuation then ended the new one and reported a false timeout — after which
  nothing registered when the user quit OpenChamber as instructed, and nothing said so.
  It was reachable by merely selecting a different project row.

Item 11 is the regression test for the second one: it was confirmed to fail against the
pre-fix build and to pass against the fixed one.

### Not verified here

**The full write path — quit OpenChamber, click an ○ project, watch it register and
relaunch.** It is the one remaining step and it is deliberately left to you, because
it requires quitting OpenChamber from its tray icon and that is your window, not
ours. The steps:

1. Right-click the OpenChamber tray icon → **Quit**, and confirm its dialog. Closing
   the window is not enough — that only hides it to the tray and the process stays.
2. In SessionLauncher, switch to 项目, pick a row marked **○**, and click 打开整套会话.
3. Expect, in order: a backup appearing under
   `%LOCALAPPDATA%\SessionLauncher\backups\settings-*.json`; OpenChamber starting; and
   its sidebar showing that project node with its conversations beneath it.
4. The row's mark becomes **●** after the next Reload.

To confirm nothing was lost, compare the top-level key count of
`%USERPROFILE%\.config\openchamber\settings.json` before and after — it is **58**
today and `VerifyRegistered` will have thrown rather than let a write drop any.
- Registering a project while OpenChamber is running.
