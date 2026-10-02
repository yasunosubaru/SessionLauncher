// refresh_catalog.mjs
//
// Regenerates the SessionLauncher catalog markdown + sessions.json sidecar
// directly from the opencode SQLite database, using the BUILT-IN node:sqlite
// module (Node >= 22.5). No npm dependencies.
//
//   node refresh_catalog.mjs
//
// Database (opened READ-ONLY):
//   C:\Users\demo\.local\share\opencode\opencode.db
//   Override with env SESSIONLAUNCHER_DB (useful for tests / portability).
//
// "Top-level conversation" means session_v2.parent_id IS NULL. The subagent
// children rows are excluded.
//
// Outputs (written next to each other at the resolved catalog path):
//   TOP-LEVEL-SESSIONS.md  8 columns: # | created | updated | msgs | agent |
//                          directory | title | session id
//   sessions.json          [{id,title,directory,agent,created,updated,msgs}]

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { DatabaseSync } from 'node:sqlite';

import { escapeCell, resolveCatalogWritePath } from './lib/catalog.mjs';

const DEFAULT_DB_PATH = 'C:\\Users\\demo\\.local\\share\\opencode\\opencode.db';

const TABLE_HEADER =
  '| # | created | updated | msgs | agent | directory | title | session id |';
const TABLE_SEPARATOR =
  '| --- | --- | --- | --- | --- | --- | --- | --- |';
const TITLE_LINE = '# TOP-LEVEL-SESSIONS';

/**
 * Format epoch milliseconds as 'YYYY-MM-DD HH:MM' in LOCAL time.
 *
 * Returns '' for anything that is not a real instant. The check has to be for
 * null/undefined/NaN as a NUMBER, not for NaN on the constructed Date:
 * `new Date(null)` is epoch zero, which is a perfectly valid Date and formats as
 * 1970-01-01. session_v2.time_created is nullable, so without this a session with
 * no creation time is written as 1970 and the GUI displays it as fact.
 *
 * @param {number|null|undefined} epochMs
 * @returns {string}
 */
function formatLocal(epochMs) {
  if (epochMs === null || epochMs === undefined) return '';
  if (typeof epochMs !== 'number' || !Number.isFinite(epochMs)) return '';
  const d = new Date(epochMs);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n) => String(n).padStart(2, '0');
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}`
  );
}

/**
 * Query the database for top-level sessions and their message counts.
 * @param {string} dbPath
 * @returns {{id:string,title:string,directory:string,agent:string,time_created:number,time_updated:number,msgs:number}[]}
 */
function readSessions(dbPath) {
  const db = new DatabaseSync(dbPath, { readOnly: true });
  try {
    return db
      .prepare(
        `SELECT s.id,
                s.title,
                s.directory,
                s.agent,
                s.time_created,
                s.time_updated,
                (SELECT COUNT(*) FROM session_message m WHERE m.session_id = s.id) AS msgs
           FROM session_v2 s
          WHERE s.parent_id IS NULL
          ORDER BY s.time_updated DESC`,
      )
      .all();
  } finally {
    db.close();
  }
}

/**
 * Regenerate the catalog. Returns a summary of what was written.
 *
 * `options.dbPath` overrides the database. The test suite needs it, because the
 * default is the user's real opencode.db and a test must never read or write it.
 * @param {{dbPath?: string}} [options]
 * @returns {{count:number, markdownPath:string, jsonPath:string}}
 */
export function generate(options = {}) {
  const dbPath = options.dbPath || process.env.SESSIONLAUNCHER_DB || DEFAULT_DB_PATH;

  let rows;
  try {
    rows = readSessions(dbPath);
  } catch (err) {
    throw new Error(`Could not read opencode database at ${dbPath}: ${err.message}`);
  }

  const markdownPath = resolveCatalogWritePath();
  const jsonPath = join(dirname(markdownPath), 'sessions.json');

  const lines = [TITLE_LINE, '', TABLE_HEADER, TABLE_SEPARATOR];
  const sidecar = [];

  const wrap = (v) => '`' + escapeCell(v) + '`';

  rows.forEach((row, index) => {
    const created = formatLocal(row.time_created);
    const updated = formatLocal(row.time_updated);
    const msgs = row.msgs ?? 0;
    lines.push(
      `| ${index + 1} | ${created} | ${updated} | ${msgs} | ${escapeCell(row.agent)} | ` +
        `${wrap(row.directory)} | ${escapeCell(row.title)} | ${wrap(row.id)} |`,
    );
    sidecar.push({
      id: row.id,
      title: row.title,
      directory: row.directory,
      agent: row.agent,
      created: row.time_created,
      updated: row.time_updated,
      msgs,
    });
  });

  mkdirSync(dirname(markdownPath), { recursive: true });
  writeFileSync(markdownPath, lines.join('\n') + '\n', 'utf8');
  writeFileSync(jsonPath, JSON.stringify(sidecar, null, 2) + '\n', 'utf8');

  return { count: rows.length, markdownPath, jsonPath };
}

// CLI entry: only run when executed directly, not when imported.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = generate();
    process.stderr.write(
      `refresh_catalog: wrote ${result.count} session(s) to ${result.markdownPath}\n`,
    );
    process.stderr.write(`refresh_catalog: sidecar ${result.jsonPath}\n`);
  } catch (err) {
    process.stderr.write(`refresh_catalog: ${err.message}\n`);
    process.exitCode = 1;
  }
}
