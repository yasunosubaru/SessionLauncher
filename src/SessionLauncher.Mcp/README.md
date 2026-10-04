# SessionLauncher.Mcp

A **zero-dependency** MCP (Model Context Protocol) server that exposes the
top-level opencode session catalog over stdio, plus a catalog generator that
reads the opencode SQLite database directly.

No `npm install` is required or possible: there are no dependencies. MCP
JSON-RPC 2.0 is implemented by hand.

## Files

```
src/SessionLauncher.Mcp/
├── package.json          metadata, `start` / `refresh` scripts (no deps)
├── server.mjs            MCP stdio server (JSON-RPC by hand)
├── lib/
│   └── catalog.mjs       catalog resolution order + markdown parser (shared)
├── refresh_catalog.mjs   regenerate the catalog from opencode.db (node:sqlite)
└── README.md
```

## Running

```powershell
node server.mjs          # start the MCP server on stdio
node refresh_catalog.mjs # regenerate the catalog + sessions.json
```

Register it with an MCP host as a stdio server whose `command` is `node` and
whose single argument is the absolute path to `server.mjs`.

## Protocol

- stdin: one JSON-RPC message per line.
- stdout: one JSON-RPC **response** per line. Nothing else is ever written to
  stdout — all logging goes to stderr.
- Handshake: `initialize` → `notifications/initialized`, then `tools/list` and
  `tools/call`. Unknown methods return a JSON-RPC error, never silence.

## Tools

| Tool | Arguments | Result |
| --- | --- | --- |
| `list_sessions` | `{ query?, limit? }` | JSON array of `{id,title,directory,agent,updated,msgs}`. `query` is a case-insensitive substring over title/id/directory; `limit` defaults to 50. |
| `open_session` | `{ sessionId, target }` | Launches a session. `target` is `openchamber` or `opencode`. Validates `sessionId` against the catalog first. |
| `refresh_catalog` | `{}` | Regenerates the catalog from `opencode.db`; returns `{count, path, sessionsJson}`. |

### `open_session` targets

- `openchamber` — spawns detached/hidden
  `…\@openchamberelectron\OpenChamber.exe "openchamber://session/<id>"`.
  Override the executable with env `SESSIONLAUNCHER_OPENCHAMBER_EXE`.
- `opencode` — spawns detached/hidden `opencode -s <id>`. The child **must not
  inherit** `OPENCODE_CONFIG` (that config has an unsupported `plugins` key and
  every invocation aborts while it is set), so the server copies the
  environment and deletes `OPENCODE_CONFIG` before spawning.

## Catalog resolution order

Defined once in `lib/catalog.mjs`; the WPF host must use the same order. First
path that exists wins:

1. `%SESSIONLAUNCHER_CATALOG%`
2. `%LOCALAPPDATA%\SessionLauncher\TOP-LEVEL-SESSIONS.md`
3. `<exeDir>/data/TOP-LEVEL-SESSIONS.md`
4. `<projectRoot>/data/TOP-LEVEL-SESSIONS.md`

If none exist, the reader throws an error naming every path tried. The
generator writes to `%SESSIONLAUNCHER_CATALOG%` when set, else to (2) if its
directory exists, else to (4).

## Regenerating (`refresh_catalog.mjs`)

Reads `session_v2 WHERE parent_id IS NULL` (subagent children are excluded),
formats `time_updated` (epoch ms) as local `YYYY-MM-DD HH:MM`, escapes `|` as
`\|`, wraps directory and id in backticks, and writes:

- `TOP-LEVEL-SESSIONS.md` — drop-in replacement for the existing table.
- `sessions.json` — `[{id,title,directory,agent,updated,msgs}]`, where
  `updated` is epoch ms and `msgs` is the count of `session_message` rows.

Database: `%USERPROFILE%\.local\share\opencode\opencode.db`, opened
**read-only**. Override with env `SESSIONLAUNCHER_DB`.

## Environment variables

| Variable | Purpose |
| --- | --- |
| `SESSIONLAUNCHER_OPENCHAMBER_EXE` | Override the OpenChamber executable path. |
| `SESSIONLAUNCHER_DB` | Override the opencode database path. |
