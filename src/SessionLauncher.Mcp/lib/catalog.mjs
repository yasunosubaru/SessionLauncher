// lib/catalog.mjs
//
// Catalog resolution + parsing for SessionLauncher.Mcp.
//
// The catalog is a GitHub-flavored markdown table listing top-level opencode
// sessions. This module owns the SINGLE resolution order used by both the JS
// MCP server and the (separate) WPF host: whoever reads the catalog must find
// the same file. Do not duplicate the order anywhere else.
//
// Resolution order (first path that exists wins):
//   1. %SESSIONLAUNCHER_CATALOG%                     (explicit override)
//   2. <exeDir>/data/TOP-LEVEL-SESSIONS.md
//   3. %LOCALAPPDATA%\SessionLauncher\TOP-LEVEL-SESSIONS.md
//   4. <projectRoot>/data/TOP-LEVEL-SESSIONS.md
//
// Paths 2 and 4 are derived from this file's own location, so the layout is
// assumed to be:
//   <projectRoot>/src/SessionLauncher.Mcp/lib/catalog.mjs
// where exeDir = <projectRoot>/src/SessionLauncher.Mcp and the project root is
// the SessionLauncher directory (the one containing `data/`).

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Directory containing this module: <projectRoot>/src/SessionLauncher.Mcp/lib */
const libDir = dirname(fileURLToPath(import.meta.url));
/** The MCP server directory: <projectRoot>/src/SessionLauncher.Mcp */
export const exeDir = dirname(libDir);
/** The project root: the directory that contains `data/` and `src/`. */
export const projectRoot = dirname(dirname(exeDir));

const CATALOG_FILENAME = 'TOP-LEVEL-SESSIONS.md';

/**
 * The canonical store, shared by the GUI and this MCP server.
 *
 * It leads the resolution order and is where refresh_catalog writes. The exe-local
 * copy under <exeDir>/data is only a portable fallback: if the writer targeted it
 * while the reader preferred the canonical file, the two would silently drift
 * apart and show different session counts.
 *
 * It used to be a hardcoded absolute path on the author's machine — which made the
 * published server point every other user at a directory that does not exist on
 * their disk, and printed their volume layout in every error message. Per-user
 * application data is the portable equivalent: one directory the current user can
 * certainly write to.
 */
const CANONICAL_CATALOG_PATH = process.env.LOCALAPPDATA
  ? join(process.env.LOCALAPPDATA, 'SessionLauncher', CATALOG_FILENAME)
  : join(projectRoot, 'data', CATALOG_FILENAME);

/**
 * All candidate catalog paths, in resolution order. Entries may be null when the
 * required environment variable is missing.
 * @returns {(string|null)[]}
 */
export function catalogCandidatePaths() {
  const override = process.env.SESSIONLAUNCHER_CATALOG;
  return [
    override || null,
    CANONICAL_CATALOG_PATH,
    join(exeDir, 'data', CATALOG_FILENAME),
    join(projectRoot, 'data', CATALOG_FILENAME),
  ];
}

/**
 * Resolve the catalog path for reading. Returns the first candidate that
 * exists, or throws an Error naming every path that was tried.
 * @returns {string}
 */
export function resolveCatalogPath() {
  const candidates = catalogCandidatePaths();
  for (const candidate of candidates) {
    if (candidate && existsSync(candidate)) return candidate;
  }
  const tried = candidates
    .map((p, i) => `  ${i + 1}. ${p ?? '(skipped: LOCALAPPDATA not set)'}`)
    .join('\n');
  throw new Error(`Could not find ${CATALOG_FILENAME}. Tried:\n${tried}`);
}

/**
 * Resolve where the generator should WRITE the catalog.
 *
 * Always the canonical path when its directory exists, so the GUI and this server
 * keep converging on one file. Falls back to the project data directory on a
 * machine without that volume.
 *
 * SESSIONLAUNCHER_CATALOG overrides everything. It is how a user points the tool
 * at their own catalog, and it is also what the test suite sets: without it a
 * call to generate() rewrites the live catalog that the GUI and this server both
 * read, so a failing test would destroy the very file it is asserting about.
 * @returns {string}
 */
export function resolveCatalogWritePath() {
  const override = process.env.SESSIONLAUNCHER_CATALOG;
  if (override) return override;
  if (existsSync(dirname(CANONICAL_CATALOG_PATH))) return CANONICAL_CATALOG_PATH;
  return join(projectRoot, 'data', CATALOG_FILENAME);
}

/**
 * Split a table row into cells on UNESCAPED pipes only. `\|` is unescaped to a
 * literal pipe; every other backslash is preserved (Windows paths in directory
 * cells must survive untouched).
 * @param {string} line
 * @returns {string[]}
 */
function splitRow(line) {
  const cells = [];
  let cur = '';
  let i = 0;
  while (i < line.length) {
    const c = line[i];
    if (c === '\\' && line[i + 1] === '|') {
      cur += '|';
      i += 2;
      continue;
    }
    if (c === '|') {
      cells.push(cur);
      cur = '';
      i += 1;
      continue;
    }
    cur += c;
    i += 1;
  }
  cells.push(cur);
  // Drop the empty leading/trailing cells produced by the outer pipes.
  if (cells.length && cells[0].trim() === '') cells.shift();
  if (cells.length && cells[cells.length - 1].trim() === '') cells.pop();
  return cells.map((c) => c.trim());
}

/** Strip surrounding backticks from a cell (directory / session id cells). */
function stripBackticks(cell) {
  return cell.replace(/^`+/, '').replace(/`+$/, '');
}

/** Columns the table must carry, whatever order they appear in. */
const REQUIRED_COLUMNS = ['#', 'updated', 'msgs', 'agent', 'directory', 'title', 'session id'];

/**
 * True for the `| --- | --- |` separator row.
 */
function isSeparator(cells) {
  return cells.length > 0 && cells.every((c) => /^:?-{2,}:?$/.test(c));
}

/**
 * Map a header row's column names to their indexes, or null when it is not a
 * header this parser understands.
 *
 * Columns are matched by NAME, never by position. The catalog gained a `created`
 * column, and both readers of this shared file — this one and the WPF app's —
 * had the original seven fields hardcoded to indexes. A positional reader of a
 * file it did not write is a reader that breaks the moment the file is
 * regenerated, and it breaks the other reader too, because they share one
 * artifact.
 *
 * `created` is deliberately NOT required: a catalog written before it existed
 * must keep parsing, reporting no creation time rather than nothing at all.
 *
 * @param {string[]} cells
 * @returns {Record<string, number>|null}
 */
export function columnMap(cells) {
  if (!Array.isArray(cells) || cells.length === 0) return null;

  const map = Object.create(null);
  for (let i = 0; i < cells.length; i++) {
    const name = String(cells[i]).toLowerCase();
    // First writer wins, so a duplicated name cannot shadow the real column.
    if (name && !(name in map)) map[name] = i;
  }

  for (const required of REQUIRED_COLUMNS) {
    if (!(required in map)) return null;
  }
  return map;
}

/**
 * Parse the markdown catalog into session records.
 *
 * `created` and `updated` are the RAW trimmed cell text, not dates. This parser
 * has always passed `updated` through as a string, and giving one timestamp a
 * parsed type while leaving the other as text would change the contract for
 * every caller for no reason.
 *
 * @param {string} markdown
 * @returns {{id:string,title:string,directory:string,agent:string,created:string,updated:string,msgs:number}[]}
 */
export function parseCatalog(markdown) {
  const sessions = [];
  let map = null;

  for (const rawLine of markdown.split(/\r?\n/)) {
    const line = rawLine.trimEnd();
    if (!line.startsWith('|')) continue;

    const cells = splitRow(line);
    if (isSeparator(cells)) continue;

    const candidate = columnMap(cells);
    if (candidate) {
      map = candidate;
      continue;
    }

    // Rows before any recognisable header cannot be read at all.
    if (!map) continue;

    // A row that cannot carry an id is unusable; a row that merely lost a later
    // column still yields everything it kept.
    if (map['session id'] >= cells.length) continue;

    const cell = (name) => (map[name] < cells.length ? cells[map[name]] : '');
    const msgs = Number.parseInt(cell('msgs'), 10);

    sessions.push({
      id: stripBackticks(cell('session id')),
      title: cell('title'),
      directory: stripBackticks(cell('directory')),
      agent: cell('agent'),
      // '' when the column is absent, which is what the 7-column layout gives.
      created: cell('created'),
      updated: cell('updated'),
      msgs: Number.isNaN(msgs) ? 0 : msgs,
    });
  }
  return sessions;
}

/**
 * Resolve, read and parse the catalog in one step.
 * @returns {{id:string,title:string,directory:string,agent:string,created:string,updated:string,msgs:number}[]}
 */
export function loadCatalog() {
  const path = resolveCatalogPath();
  const text = readFileSync(path, 'utf8');
  return parseCatalog(text);
}

/** Escape a value for use inside a markdown table cell. */
export function escapeCell(value) {
  return String(value ?? '')
    .replace(/\r?\n/g, ' ')
    .replace(/\|/g, '\\|');
}
