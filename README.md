# SessionLauncher

A Windows launcher for OpenCode conversations, plus an MCP server so opencode can
drive the same catalog.

Two halves, deliberately independent:

| Half | Stack | Output |
|---|---|---|
| `src/SessionLauncher.App` | .NET 10 WPF, **zero NuGet packages** | `SessionLauncher.exe` |
| `src/SessionLauncher.Mcp` | Node, **zero npm packages** | MCP stdio server (`server.mjs`) |

Both build and run offline.

> **Why WPF and not SwiftUI.** SwiftUI does not exist on Windows — the Swift
> toolchain for Windows ships Foundation only, with no SwiftUI or AppKit — and no
> Swift toolchain is installed on this machine either. WPF is the closest native
> declarative GUI that actually produces a Windows `.exe` here.

## What it does

`TOP-LEVEL-SESSIONS.md` is a markdown table of every **top-level** OpenCode
conversation. Both halves read it, filter it, and can hand a conversation to
OpenChamber or to the opencode CLI.

## The catalog

Canonical location, shared by both halves:

```
<catalog dir>\TOP-LEVEL-SESSIONS.md
```

Fallback order (first hit wins), implemented identically in
`SessionCatalog.ProbeCandidatePaths` and `lib/catalog.mjs`:

1. `<catalog dir>\TOP-LEVEL-SESSIONS.md`  ← canonical
2. `<exeDir>\data\TOP-LEVEL-SESSIONS.md`          ← portable copy, shipped next to the exe
3. `%LOCALAPPDATA%\SessionLauncher\TOP-LEVEL-SESSIONS.md`
4. `<repo>\apps\SessionLauncher\data\TOP-LEVEL-SESSIONS.md`

The canonical path leads deliberately. An earlier revision preferred the exe-local
copy, so `refresh_catalog` wrote to one file while the GUI read another and the two
silently drifted apart.

Regenerate from the database:

```powershell
node src\SessionLauncher.Mcp\refresh_catalog.mjs
```

Reads `C:\Users\demo\.local\share\opencode\opencode.db` **read-only** via the
built-in `node:sqlite`, selects `session_v2 WHERE parent_id IS NULL` (the ~228
subagent child rows are excluded), and writes both the markdown table and a
`sessions.json` sidecar.

## The GUI

```
src\SessionLauncher.App\bin\Release\net10.0-windows\SessionLauncher.exe
```

- **Font scaling** — `A-` / `A` / `A+`, 80 %…200 %, persisted
- **Search** — Everything-style ranking over title / id / folder / agent
- **Checkbox multi-select** — tick rows; the ticks survive filtering
- **Open in OpenChamber** (default; Enter or double-click)
- **Open in opencode** — `opencode -s <id>`, one window per selected conversation
- **Copy ids** — every selected session id, one per line
- **Project / workspace** — Explorer, terminal, editor, or a project-rooted `opencode` TUI
- **Language toggle** — `EN` / `中` switches the whole window
- **Project view** — every project opencode has worked in, read from its own logs,
  with nine sort orders

### Project view

`会话` / `项目` at the top of the window switches between the conversation list and a
list of **projects**, where a project is "a set of sessions that share a directory".
That is the same shape as OpenChamber's own sidebar: a project node with its
conversations grouped beneath it, and the conversation as the smallest unit.

The list is built by grouping the **catalog** (`Services\ProjectCatalog.cs`), not by
scraping opencode's logs. An earlier revision read
`%USERPROFILE%\.local\share\opencode\log\*.log` and found 135 directories, most of
them long dead, which is not what a project list is. Grouping the 141 catalog sessions
gives 40 projects, each with its sessions attached — which is what "open a project"
actually needs, because opening it means opening those sessions.

#### One grouping rule, and why

OpenChamber gives every conversation its own working directory:

```
C:\Users\demo\.config\openchamber\chats\2026-01-01\session-abc123-…
```

The catalog records that directory, so grouping on it produced 25 rows named
`session-abc123-…`, each holding exactly one conversation, which buried the handful
of directories a person would actually call a project. `ProjectCatalog.ResolveProjectDirectory`
collapses everything at or below `openchamber\chats` onto the directory above it, so
those become one row, `OpenChamber 会话`, holding 37 conversations. The marker is
matched rather than a `session-<uuid>` leaf pattern, so no knowledge of OpenChamber's id
format is needed and no variant of it can slip through.

#### Colours

Each project gets a folder icon in **OpenChamber's own palette**, so the same project is
recognisably the same in both apps. `PROJECT_COLORS` in
`packages/ui/src/lib/projectMeta.ts` does not name colours, it points at CSS variables,
and the labels do not match the values — `keyword` is labelled *Purple* but
`--syntax-keyword` resolves to `#34983a`, a green. The hexes in `ProjectCatalog.Colors`
are read from the default dark theme the screenshots were taken with,
`openchamber-dark.json`:

| Key | Label | Resolves to |
|---|---|---|
| `keyword` | Purple | `#34983a` |
| `string` | Green | `#d58373` |
| `number` | Pink | `#279e93` |
| `type` | Gold | `#479cb1` |
| `constant` | Cyan | `#c69457` (‘constant’ points at `syntax.variable`) |
| `comment` | Muted | `#728772` |
| `error` | Red | `#da5b4a` |
| `primary` | Blue | `#da7c47` (OpenChamber's brand orange) |
| `success` | Green | `#76ad4f` |

Assignment reproduces OpenChamber's least-used-first `pickAutoColor` but deterministically
— OpenChamber picks randomly among the least-used keys, which would give a project a
different colour on every launch. A project OpenChamber has already coloured keeps that
colour, read from its `settings.json`; that read is the only contact this app has with
that file, and it never writes it.

Columns: name, session count, last used, colour name, and the full path. The colour name
is shown next to the swatch so the choice is visible rather than implied.

#### Sort orders

| Mode | Order |
|---|---|
| 最近使用 / Recently used | `LastUsed` desc, then session count — the default |
| 最少使用 / Least recently used | `LastUsed` asc |
| 最多会话 / Most sessions | `SessionCount` desc |
| 最活跃 / Most active | `MessageCount` desc, a proxy for workload |
| 名称 A→Z / Name A-Z | label, case-insensitive |
| 名称 Z→A / Name Z-A | label descending |
| 最早 / Oldest first | `FirstUsed` asc |
| 最新 / Newest first | `FirstUsed` desc |
| 路径深度 / Path depth | shallowest first, then label |

`FirstUsed` and `LastUsed` are the oldest and newest session in the project, and they
cannot disagree: the earlier log-derived record had two independent timestamps and
happily described a project first seen in September and last used in June, which is
impossible. The fixture in `ProjectSort.RunSelfTest` is built from real time bands for
that reason.

Every chain ends in an ordinal comparison of `Path`. That tiebreak is load-bearing:
`OrderBy` is a *stable* sort, so projects that tie on every visible key would come back
in input order, and the input order changes between scans. Without it the list
reshuffles itself on every refresh for reasons the user cannot see. The self-test
asserts every mode is independent of input order.

`ProjectSort.Toggle` is an involution over all nine modes. Pairing was forced by that
requirement: `LastUsed`↔`LeastUsed` and `Newest`↔`Oldest` are the natural pairs, and
neither can also answer for the other, which is what the first version did. `PathDepth`
has no directional sense and is its own partner.

#### Opening a project

Select a row, then Explorer / terminal / editor / opencode / workspace, or double-click
for opencode. A directory that no longer exists is reported rather than shelled out to.

The action bar holds two rows, and putting both in one `Auto` column made their widths
**add**: the bar's right edge ran to x=2088 inside a 1500 px window, so the buttons were
laid out off-screen and could not be clicked. They are now separate rows in a grid.

The view, sort order and hide-missing flag are all persisted. That is not a nicety:
switching language **rebuilds the window**, so a field-only choice would drop the user
back onto the session list every time they translate the UI.

### Workspaces

`工作区` opens a **VS Code multi-root workspace**, and the evidence for what that means
is worth recording, because the name is misleading:

- **opencode has no workspace concept.** `opencode debug scrap` is documented as
  "list all known projects", and each entry is `{id, worktree, sandboxes}` — the
  worktree *is* the project. There is no workspace flag in `opencode --help`,
  `session --help` or `debug --help`.
- **OpenChamber does**, but it is a VS Code workspace: its bundle calls
  `vscode.addWorkspaceFolder(...)` and `syncVSCodeWorkspaceFolder(...)`.
- **Neither has a deep link for it.** The only `openchamber://` literals in `app.asar`
  are `connect`, `focus` and `session`. The bundle's other entry point,
  `parseDeepLinkHash`, is a GitHub-style `operations` / `operations-tag` scroll anchor
  and cannot open anything. So a workspace has to go through the shell.

So `Services\WorkspaceService.cs` does two things:

1. looks for an existing `.code-workspace` in or just under the selected projects
   (depth 2, skipping reparse points, so a junction cannot make the walk unbounded)
2. otherwise writes a multi-root one from the selection, to
   `%LOCALAPPDATA%\SessionLauncher\workspaces\`, and hands it to `code`

A malformed workspace file is still listed rather than thrown on: these are user files
that may be mid-edit, and a launcher that refuses to start over one is useless.

> VS Code 1.137.0 is installed here, but under `%LOCALAPPDATA%\Programs\Microsoft VS
> Code\`, not `C:\Program Files\`. `EditorCommand()` only probed the system paths, so
> "open in editor" silently opened Notepad++ on a machine that has VS Code. Both the
> per-user and system layouts are probed now, and the per-user one first.

### Font scaling

`A-` / `A` / `A+` step the scale, and the percentage is shown next to the buttons.
The setting persists in `sessionlauncher.settings.json` next to the exe.

Scaling changes `FontSize` **and** the `GridView` column widths. Font scaling alone is
a trap: at 140 % the default 150 px timestamp column clipped `2026-10-01 12:48` into
`2026-10-01 12:4…`, so the scale is applied to the columns as well
(`MainWindow.ApplyColumnWidths`).

### Search

`Services\SessionSearch.cs`. Scoped to the catalog only — it never touches the
database, the filesystem, or the network.

Tokens are ANDed; each token is scored per field, and the best field wins:

| Match | Score |
|---|---|
| whole value equals the token | 1000 |
| token starts a word | 600 |
| token appears anywhere | 400 |
| token is a rune-wise subsequence | 200 |

Field weights: title 10, id 8, directory 6, agent 4. Ordering is fully
deterministic, and matched ranges in the title are highlighted in place.

Two details that are easy to get wrong and are both covered by tests:

- **The winning field is chosen on the weighted score, not on raw match quality.**
  Quality-first looks correct and is not: a word-prefix hit in the *directory*
  (600 × 6 = 3600) would beat a substring hit in the *title* (400 × 10 = 4000), so a
  session whose title literally contains the query ranked below one that only
  matched on its folder name. Reproduced on the real catalog — searching `sample` put
  the title matches `ses_4a1b2c3d` and `ses_9z8y7x6w` below sessions that do not
  mention `sample` in the title at all. `SelfTest` pins this.
- **`IsSubsequence` must confirm the needle is fully consumed.** The naive
  `!needle.MoveNext()` reading is right, but returning on the positive reading
  matches on the first character alone and makes the fuzzy tier swallow everything.

### Checkbox multi-select

The first column is a `CheckBox` bound `TwoWay` to `SessionRow.IsChosen`. Ctrl-click
and Shift-click are deliberately **not** the mechanism — a checkbox is unambiguous
about intent and survives a filter that hides the row.

Ticks live in a `HashSet<string>` of session ids on the window, not as a flag on the
row objects, because filtering rebuilds the item collection. Rows mirror the set
through `IsChosen`, which raises `INotifyPropertyChanged` — note that UIA
`TogglePattern.Toggle()` flips `IsChecked` *without* raising `Click`, so a `Click`
handler is the wrong hook and this feature silently does nothing under automation.

No reentrancy guard is needed when restoring ticks: assigning a value equal to the
row's current one raises no notification, and the rows that do change are already in
the set, so the handler's add is a no-op.

### Project and workspace

| Button | What it runs |
|---|---|
| Explorer | `explorer.exe "<dir>"`, one per distinct folder |
| Terminal | `wt.exe -d "<dir>"`, else `cmd.exe`, for every distinct folder |
| Editor | `$SESSIONLAUNCHER_EDITOR`, else VS Code, Notepad++, notepad — every distinct folder |
| opencode | `opencode` rooted at the **first** folder, so its session list is that project's |

There is no project deep link to lean on. OpenChamber's `parseDeepLink()` accepts only
`host`, `connect` and `session`, so these actions have to go through the shell.

One window per project for the TUI, on purpose: three full-screen TUIs at once is
unusable, so the status line names the one that opened and how many were skipped.

### Language switching

`Services\Loc.cs` holds the whole zh/en table plus key constants, and
`Markup\TrExtension.cs` exposes `{markup:Tr some.key}` in XAML.

The toggle **rebuilds the window**. Markup extensions evaluate once at parse time, so
mutating a string in place cannot retranslate already-parsed text. Geometry carries
over. There is deliberately **no `Owner`** on the replacement window: closing an owner
closes every window it owns, which killed the new window the first time.

`Loc.SetLanguage` and `ApplyFontScale` must run **before** `InitializeComponent()`.

### Multi-select semantics

| Action | 1 selected | N selected |
|---|---|---|
| Open in OpenChamber | that conversation | the **most recently updated** one; the status line says so |
| Open in opencode | 1 window | N windows, staggered 400 ms; capped at 10 |
| Copy ids | 1 id | N ids, newline-separated |
| Explorer / Terminal / Editor | 1 folder | N distinct folders |
| opencode project | that project | the most recently used one, reported as such |

Double-clicking a row collapses a multi-selection to that row first — "double-click
means this one".

`Select all` deliberately covers the **whole catalog**, not just the filtered rows:
selecting from inside a filter should not silently cap a batch at the filter's size.
The status line reports both numbers.

Keyboard: `Enter` open · `Esc` clear filter, then clear selection · `Ctrl+A` select all.

### Dark-theme traps

Four separate things bite when restyling a stock WPF control. All four were found by
measuring, not by reading.- **A `ListViewItem`'s hover colour cannot be fixed from `Style.Triggers`.** Dumping
  the live theme template with `XamlWriter` shows a `MultiTrigger` on `IsMouseOver`
  whose setters target the inner border *by name* (`TargetName="Bd"`). A
  `ControlTemplate` trigger beats a `TemplateBinding`, so setting `Background` on the
  item changes nothing. `RowStyle` therefore carries its own `ControlTemplate`, and its
  triggers set literal colours on the inner border.
- **`GridViewRowPresenter` has only `Content`.** No `Columns` of its own, no
  `ContentTemplate`, no `ContentTemplateSelector`. Get `Columns` off the ancestor
  `ListView`.
- **A `CheckBox` with `IsChecked="True"` in XAML raises `Checked` *during*
  `InitializeComponent`**, before later elements in the tree exist. The handler then
  dereferences null and the app dies at startup with no visible error. An
  `_initializing` flag fences off the whole load.
- **A `ContentPresenter` in a combo template measures to zero width** unless
  `HorizontalAlignment` is set explicitly; it defaults to `Left`. And with
  `DisplayMemberPath` set, the closed box renders `SelectionBoxItemTemplate`, not
  `ItemTemplate`.

Two more that are compile-time rather than runtime: an XML comment may not contain
`--`, and a `StaticResource` referenced by `BasedOn` must be declared *earlier* in the
document — getting that backwards is a runtime `XamlParseException`, not a build error.

### Glass

`ApplyDarkTitleBar` sets `DWMWA_SYSTEMBACKDROP_TYPE` (38) to **Acrylic** (3), falling
back to Mica (2), alongside immersive dark mode. Requires Windows 11 22H2+; this
machine is 25H2 (build 26200). Every call is best-effort and the app is fine without it.

The attribute on its own changes nothing. DWM draws the backdrop, and then WPF paints
over it — so **every brush in the theme carries an alpha channel**. The alphas are
deliberately unequal:

| Layer | Alpha | Why |
|---|---|---|
| root `BgGradient` | ~70 % | most opaque, so long paths stay readable |
| list rows | ~91 % | nearly solid; this is what protects text contrast |
| panels / toolbar | ~60 % | the most transparent, which is what reads as glass |

Going opaque again is exactly what killed the glass feel. Acrylic is also chosen over
Mica on purpose: Mica is tuned for apps with a full title bar and reads nearly flat at
this window size.

The root is a top-lit gradient rather than a flat fill. A pane of glass lit from above
is never one colour, and that falloff is most of what sells it once the tint is in place.

Measured after the change, worst case 3.85:1 (the muted hint, AA-large), row titles
8.97:1. Hover is a low-alpha white overlay rather than a flat colour, so the row
brightens the way glass does and the backdrop still shows through it.

**`PrintWindow` cannot show any of this.** It flattens the DWM backdrop entirely, so
the glass has to be verified with `tools\shot-screen.ps1`.

### Measuring the UI

`tools\shot.ps1` uses `PrintWindow`, and **it cannot see mouse-over state**: in a
scratch window, capturing with the pointer over a row and with it off produced
byte-identical PNGs. Any hover verification done with it is measuring nothing.

`tools\shot-screen.ps1` grabs the real composited screen instead and makes the window
TOPMOST, because `SetForegroundWindow` is refused when the caller is not the foreground
process and hit-testing has to reach the window for `IsMouseOver` to become live. When
sampling a fill, take the *dominant* colour over a row band rather than single pixels —
single pixels land on glyph antialiasing and read as a completely different colour.
A plain screen grab is only usable once the window is genuinely on top; otherwise it
photographs the wallpaper.

### Crashes

Unhandled exceptions are appended to `SessionLauncher.crash.log` next to the exe
instead of vanishing silently. A WPF app that dies on a bad XAML value with no trace
is close to undebuggable, so keep this in mind when editing `MainWindow.xaml` —
`GridViewColumn.Width` is a `double`, not a `GridLength`, so `*` there is a parse
error that only shows up at runtime.

### Launching shells and editors

Three things bite here, all found by testing the buttons rather than by reading them:

- **`powershell.exe` is not in `System32`.** It lives in
  `System32\WindowsPowerShell\v1.0\`. Probing the obvious path misses it silently and
  drops you through to `cmd.exe`.
- **App Execution Aliases need the shell.** `wt.exe` under
  `%LOCALAPPDATA%\Microsoft\WindowsApps` is a stub that hands off to the terminal
  broker and exits immediately. Launched with `UseShellExecute = false` it appears to
  succeed and then does nothing, so a process count before/after proves nothing either
  way. `OpenTerminal` and `OpenEditor` use `UseShellExecute = true`.
- **One status string for three actions is a lie.** They all reported "opened N
  folders in Explorer" until the target name was factored into the message key
  (`Loc.StatusOpenedIn`), so a terminal launch announced itself as Explorer.

### Verifying the UI

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\shot.ps1 -Process SessionLauncher -Out .\window.png
```

`PrintWindow` with `PW_RENDERFULLCONTENT`, not a screen grab — a screen grab kept
capturing a stuck Windows Start menu instead of the app. Verify clicks through UIA by
`AutomationId`; matching on `Name` is fragile, and note that `[char] + [char]` in
PowerShell is numeric addition, not string concatenation.

### Self tests

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run-selftest.ps1
```

231 assertions, headless, no NUnit: the search engine, `ProjectCatalog`,
`ProjectSort`, and `AppSettings` round-trip / corruption recovery / clamping / float
drift. The script stages the non-UI sources into a throwaway console project —
**adding a service means adding it to the `$sources` list** or it silently stops being
tested. Two services were missed this way already: `AppSettings` gained an `AppView`
property and the staged build failed on the missing `Models\AppView.cs`.

### Icon

`src\SessionLauncher.App\Assets\SessionLauncher.ico`, embedded via `ApplicationIcon`.
Regenerate after changing the design:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\make-icon.ps1
```

It renders each size natively (16…256) and packs them as PNG payloads into a
Vista-style ICO. Two PowerShell traps are already handled in that script and are
worth knowing before you touch it: a `byte[]` returned from a function is unrolled
into individual bytes unless you prefix it with `,`, and `$PSScriptRoot` is not yet
bound while `param` defaults are evaluated.

### How OpenChamber is driven

```
OpenChamber.exe "openchamber://session/<sessionId>"
```

OpenChamber's main process parses the URL, takes the hostname as the action and the
joined path as the value, and for action `session` emits `openchamber:open-session`
to the renderer, which calls `setCurrentSession(id, null)`. If OpenChamber is
already running this arrives as a `second-instance` event and just focuses the
existing window. Verified end to end, including from this GUI.

### One environment landmine

Any code that shells out to `opencode` must **delete `OPENCODE_CONFIG` from the
child environment first**. On this machine it points at an OpenChamber-managed
config carrying an unsupported `plugins` key, and *every* `opencode` invocation
aborts while it is set:

```
Error: Configuration is invalid at ...\opencode.managed.json
Unrecognized key: plugins
```

Both halves do this (`LauncherService.RemoveOpenCodeConfig`,
`openSession` in `server.mjs`).

> Related: `opencode export <id>` still fails for project-bound sessions
> (`Session not found`) even though `opencode -s <id>` works. The export path reads
> the legacy `session` table, which happens to hold exactly the 207 `global`-project
> sessions. Do not use `export` to test whether a session exists.

## The MCP server

```
node src\SessionLauncher.Mcp\server.mjs
```

JSON-RPC 2.0 over stdio, implemented by hand — no `@modelcontextprotocol/sdk`, no
zod. stdout carries protocol JSON only; all logging goes to stderr.

| Tool | Arguments | Returns |
|---|---|---|
| `list_sessions` | `query?`, `limit?` | JSON array of `{id,title,directory,agent,updated,msgs}` |
| `open_session` | `sessionId`, `target` (`openchamber` \| `opencode`) | confirmation |
| `refresh_catalog` | — | `{count, path, sessionsJson}` |

Register it in `~/.config/opencode/opencode.jsonc`:

```jsonc
"sessionlauncher": {
  "type": "local",
  "command": ["C:\\Program Files\\nodejs\\node.exe",
              "<repo>\\apps\\SessionLauncher\\src\\SessionLauncher.Mcp\\server.mjs"],
  "enabled": true
}
```

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Refreshes the catalog, builds the GUI, and syntax-checks the MCP server.

## Layout

```
SessionLauncher/
  build.ps1
  tools/make-icon.ps1              regenerates the .ico
  tools/run-selftest.ps1           231 headless assertions
  tools/shot.ps1                   PrintWindow screenshot (cannot see hover or glass)
  tools/shot-screen.ps1            real screen grab (needed for hover and glass)
  tools/compare-search.ps1         diffs two SessionSearch implementations on the real catalog
  data/TOP-LEVEL-SESSIONS.md       repo copy of the catalog
  src/SessionLauncher.App/
    SessionLauncher.App.csproj
    App.xaml, App.xaml.cs          crash log wiring
    MainWindow.xaml, MainWindow.xaml.cs
    Markup/TrExtension.cs          {markup:Tr key}
    Models/SessionInfo.cs, AppLang.cs, AppView.cs
    Models/ProjectRow.cs           project list row
    Services/SessionCatalog.cs     markdown table parsing + probe order
    Services/LauncherService.cs    deep link / CLI / clipboard / shell openers
    Services/SessionSearch.cs      Everything-style ranking
    Services/ProjectCatalog.cs     groups sessions into projects, colours them
    Services/ProjectSort.cs        nine project orderings
    Services/WorkspaceService.cs   VS Code multi-root workspaces
    Services/Loc.cs                zh/en string table
    Services/AppSettings.cs        font scale, language, geometry, view, sort
    Services/SelfTest.cs           the assertions
    Assets/SessionLauncher.ico
  src/SessionLauncher.Mcp/
    package.json, server.mjs, lib/catalog.mjs, refresh_catalog.mjs, README.md
```

## Known limits

- **Row selection by synthetic mouse input is UNVERIFIED on this machine, and may be
  broken.** Selecting a project or session row and then pressing the action buttons is
  the primary flow, and it could not be confirmed end to end here. What is known:
  - `SelectionItemPattern.Select()` over UIA selects a row, the action buttons enable,
    and the whole business path works — verified on sample: the status line read
    `已在 OpenChamber 中打开 sample 的 11 条会话。`
  - Real clicks (`mouse_event` and `SendInput`, at row text, row blank, and the
    checkbox cell) and keyboard `Down` all failed to select, in **both** views, while
    `Tab` traversal does reach the rows and reports them as focusable.
  - `IsOffscreen` is not a usable signal here: it reported `False` for buttons that
    were rendered at x=1862 inside a window ending at x=1806. Measure the rectangle and
    compare it against the window instead.

  The distinguishing evidence is missing: a synthetic click that fails and a row that
  genuinely cannot be hit look identical from outside the process. `RowStyle` has a
  custom `ControlTemplate` whose `GridViewRowPresenter` lays out only the declared
  cells, so a click landing between columns may fall through the row; a transparent
  `Border` was added around the presenter to close that gap and the failure persisted,
  which is why this is recorded as open rather than fixed. **Test it by hand**: click a
  row, then 打开整套会话. If that works, the limitation is in the harness.

- **The action bar was rendering off-screen and is now fixed.** A `DockPanel` gives its
  leftover space to the LAST child; `ProjectPanel` sat in the middle with no `Dock` set,
  so it defaulted to `Left` and took a full-height column, pushing the action bar and
  status line to x=1862 inside a window ending at x=1806. The two views are mutually
  exclusive, so they now share one `Grid` as the final `DockPanel` child. Measured after
  the fix: buttons span x=882..1566 inside a window ending at 1614.



- **SwiftUI is unavailable on Windows.** WPF is the substitute; see the note at the top.
- **No project deep link in OpenChamber.** Only `host`, `connect` and `session` verbs
  exist, so the project actions shell out instead.
- **OpenChamber cannot be made to hold several open conversations at once.** The
  obvious approach — fire `openchamber://session/<id>` once per session and let the
  header tab strip accumulate them — does not work, for two independent reasons.
  `Header.tsx:737` computes `showHeaderMetaRow` and, whenever a project label is
  showing (which is exactly when you have opened a project), renders the title row
  *instead of* `SessionTabsStrip`, so the strip is never mounted. And nothing
  recoverable is left in storage: this machine's
  `AppData\Roaming\OpenChamber\Local Storage\leveldb` has no `session-tabs-store`
  key and no array of two or more session ids anywhere in it. So one deep link to the
  project's newest session is what actually happens, and OpenChamber then shows the
  project's whole set grouped under the project node. See
  `LauncherService.OpenSessionSetInOpenChamber`.
- **Scores are not user-visible.** `SearchHit.Score` only orders the list, so two
  engines that rank identically can still disagree on the constant. Ordering is what
  was compared, not the arithmetic.
- **ultrawork / ARIS `fan_out` does not work on this machine.** The gateway at
  `ANTHROPIC_BASE_URL` returns HTTP 403 for every Claude model on `/v1/messages`
  while `GET /models` lists 51, and `claude.cmd` on PATH is a hand-written shim that
  ignores `--model`, cannot emulate the tool-use loop, and so cannot write files.
  agent-swarm does work, and its independent `SessionSearch.cs` is what exposed the
  weighted-field ranking bug documented above.
