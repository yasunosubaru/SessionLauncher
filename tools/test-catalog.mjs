// tools/test-catalog.mjs
//
// Zero-dependency assertions for the catalog generator and its two parsers.
// Run: node tools/test-catalog.mjs   (exit 0 = pass, non-zero = fail)
//
// WHY THE OUTPUT OVERRIDE MATTERS
// resolveCatalogWritePath() returns the live catalog path (%LOCALAPPDATA%
// \SessionLauncher\TOP-LEVEL-SESSIONS.md) whenever that directory exists, and it
// does on any normal Windows install. Calling generate() without an override
// therefore rewrites the live catalog that the GUI and the MCP server both read.
// The first suite below pins an environment override so every other assertion in
// this file writes into a scratch directory instead.

import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

import { resolveCatalogWritePath, parseCatalog } from '../src/SessionLauncher.Mcp/lib/catalog.mjs';
import { generate } from '../src/SessionLauncher.Mcp/refresh_catalog.mjs';

let checks = 0;
function check(ok, what) {
  checks++;
  if (!ok) throw new Error(`test-catalog: ${what}`);
}

function checkEqual(expected, actual, what) {
  checks++;
  if (expected !== actual)
    throw new Error(`test-catalog: ${what}\n  expected: ${JSON.stringify(expected)}\n  actual:   ${JSON.stringify(actual)}`);
}

function throws(fn, what) {
  checks++;
  try {
    fn();
  } catch {
    return;
  }
  throw new Error(`test-catalog: ${what} (nothing was thrown)`);
}

function scratch() {
  return mkdtempSync(join(tmpdir(), 'sl-catalog-'));
}

/** The two tables refresh_catalog.mjs reads, with rows the assertions can name. */
function buildDb(parentDir) {
  const dir = join(parentDir, 'db');
  mkdirSync(dir, { recursive: true });
  const path = join(dir, 'opencode.db');
  const db = new DatabaseSync(path);
  db.exec(`
    CREATE TABLE session_v2 (
      id TEXT PRIMARY KEY,
      title TEXT,
      directory TEXT,
      agent TEXT,
      time_created INTEGER,
      time_updated INTEGER,
      parent_id TEXT
    );
    CREATE TABLE session_message (session_id TEXT);
  `);

  const insertSession = db.prepare(
    `INSERT INTO session_v2 (id, title, directory, agent, time_created, time_updated, parent_id)
     VALUES (?, ?, ?, ?, ?, ?, NULL)`,
  );
  const insertMessage = db.prepare(`INSERT INTO session_message (session_id) VALUES (?)`);

  // Epoch ms -> 2026-08-19 11:03 and 2026-10-01 17:39 in UTC. The assertions
  // compare the two fields to each other rather than to a formatted string, so
  // the suite does not depend on this machine's time zone.
  insertSession.run('ses_one', 'title one | with pipe', 'F:/a/b', 'build', 1787136180000, 1788387540000);
  insertSession.run('ses_two', 'title two', 'C:/x', 'plan', 1782979200000, 1788140400000);

  // A child session must be excluded: "top level" means parent_id IS NULL.
  insertSession.run('ses_child', 'a subagent', 'F:/a/b', 'build', 1787136180000, 1788387540000);
  db.prepare(`UPDATE session_v2 SET parent_id = 'ses_one' WHERE id = 'ses_child'`).run();

  insertMessage.run('ses_one');
  insertMessage.run('ses_one');
  insertMessage.run('ses_two');

  db.close();
  return path;
}

// ---- 1. the output override, so nothing below can touch the live catalog ----

{
  const dir = scratch();
  try {
    const wanted = join(dir, 'TOP-LEVEL-SESSIONS.md');
    process.env.SESSIONLAUNCHER_CATALOG = wanted;
    try {
      checkEqual(wanted, resolveCatalogWritePath(),
        'SESSIONLAUNCHER_CATALOG overrides the canonical write path');
    } finally {
      delete process.env.SESSIONLAUNCHER_CATALOG;
    }

    check(process.env.SESSIONLAUNCHER_CATALOG === undefined,
      'the override is deleted again before the next suite');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// ---- 2. the generator's table shape ----

{
  const dir = scratch();
  const previous = process.env.SESSIONLAUNCHER_CATALOG;
  try {
    const markdownPath = join(dir, 'TOP-LEVEL-SESSIONS.md');
    process.env.SESSIONLAUNCHER_CATALOG = markdownPath;

    const result = generate({ dbPath: buildDb(dir) });
    checkEqual(markdownPath, result.markdownPath,
      'generate writes to the overridden path');

    const markdown = readFileSync(markdownPath, 'utf8');
    const lines = markdown.split(/\r?\n/).filter((l) => l.length > 0);

    // Title line, header, separator, then one row per top-level session: five
    // lines for two sessions. The child session is not top level, so there are
    // two rows, not three.
    checkEqual(5, lines.length, 'title + header + separator + one row per top-level session');

    checkEqual('| # | created | updated | msgs | agent | directory | title | session id |',
      lines[1], 'the header carries eight columns');
    checkEqual('| --- | --- | --- | --- | --- | --- | --- | --- |',
      lines[2], 'the separator carries eight cells');

    const rows = lines.slice(3);
    for (const row of rows) {
      // Split on UNESCAPED pipes only: a \| inside a title is one cell's content,
      // so it must not be neutralised into a pipe first. Replacing it with a NUL
      // keeps the cell count honest without pretending the escape is a separator.
      const escaped = row.replaceAll('\\|', '\u0000');
      checkEqual(8, escaped.split('|').length - 2, `a row has 8 cells: ${row}`);
    }

    check(!rows.some((r) => r.includes('ses_child')),
      'a session with a parent is not top level and is excluded');

    // The pipe inside the title must have been escaped, or the row would split
    // into nine cells instead of eight. This is the single most fragile part of
    // the format, so it is asserted on the emitted text rather than trusted.
    const one = rows.find((r) => r.includes('ses_one'));
    check(one !== undefined, 'the first session has a row');
    check(one.includes('title one \\| with pipe'),
      'a pipe inside a title is escaped so the row still splits into 8 cells');

    const cells = (row) => row.replaceAll('\\|', '\u0000').split('|').slice(1, -1).map((c) => c.trim());
    const oneCells = cells(one);
    check(oneCells[1] !== oneCells[2],
      'the created cell differs from the updated cell');
    check(!Number.isNaN(Date.parse(oneCells[1])), 'the created cell parses as a date');
    check(!Number.isNaN(Date.parse(oneCells[2])), 'the updated cell parses as a date');
    checkEqual('2', oneCells[3],
      'the msgs cell is the message count, 2 for ses_one');

    // The sidecar carries epoch milliseconds, not formatted text.
    const sidecar = JSON.parse(readFileSync(result.jsonPath, 'utf8'));
    checkEqual(2, sidecar.length, 'the sidecar has one entry per top-level session');
    check(typeof sidecar[0].created === 'number',
      'the sidecar entry has a numeric created');
    check(typeof sidecar[0].updated === 'number',
      'the sidecar entry has a numeric updated');
    check(sidecar.every((s) => s.created !== s.updated),
      'created and updated are distinct fields, not the same value twice');
  } finally {
    if (previous === undefined) delete process.env.SESSIONLAUNCHER_CATALOG;
    else process.env.SESSIONLAUNCHER_CATALOG = previous;
    rmSync(dir, { recursive: true, force: true });
  }
}

// ---- 3. the MCP parser, on inline markdown ----
//
// The MCP server and the WPF app read the SAME file. A parser that only
// understands one layout is not a local bug: whichever reader is stricter
// decides what the other one sees.

const MD_SEVEN = [
  '| # | updated | msgs | agent | directory | title | session id |',
  '| --- | --- | --- | --- | --- | --- | --- |',
  '| 1 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |',
  '| 2 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |',
].join('\n');

const MD_EIGHT = [
  '| # | created | updated | msgs | agent | directory | title | session id |',
  '| --- | --- | --- | --- | --- | --- | --- | --- |',
  '| 1 | 2026-08-19 11:03 | 2026-10-01 17:39 | 1031 | build | `F:/a/b` | title one \\| with pipe | `ses_one` |',
  '| 2 | 2026-07-02 08:00 | 2026-01-01 09:00 | 7 | plan | `C:/x` |  | `ses_two` |',
].join('\n');

{
  // The old layout: still parses, and created is empty rather than absent.
  const s7 = parseCatalog(MD_SEVEN);
  checkEqual(2, s7.length, 'the parser reads the 7-column layout');
  checkEqual('ses_one', s7[0].id, 'the id is read from the 7-column layout');
  checkEqual('title one | with pipe', s7[0].title, 'an escaped pipe resolves in the parser too');
  checkEqual(1031, s7[0].msgs, 'the message count is read');
  checkEqual('F:/a/b', s7[0].directory, 'backticks are stripped from the directory');
  checkEqual('2026-10-01 17:39', s7[0].updated, 'updated stays raw text, as it always was');
  checkEqual('', s7[0].created, 'a layout with no created column yields an empty string');

  // The new layout.
  const s8 = parseCatalog(MD_EIGHT);
  checkEqual(2, s8.length, 'the parser reads the 8-column layout');
  checkEqual('2026-08-19 11:03', s8[0].created, 'the created cell is read');
  checkEqual('2026-07-02 08:00', s8[1].created, 'each row gets its own created value');
  checkEqual('2026-10-01 17:39', s8[0].updated, 'created did not displace updated');
  checkEqual('ses_one', s8[0].id, 'the id did not shift with the new column');
  checkEqual(1031, s8[0].msgs, 'the message count did not shift either');
  checkEqual('build', s8[0].agent, 'nor did the agent');

  // Order-independence, which is the whole reason for name mapping.
  const reversed = [
    '| # | updated | msgs | agent | title | directory | session id |',
    '| --- | --- | --- | --- | --- | --- | --- |',
    '| 1 | 2026-10-01 17:39 | 5 | build | the title | `F:/a/b` | `ses_rev` |',
  ].join('\n');
  const rev = parseCatalog(reversed);
  checkEqual(1, rev.length, 'a reordered header parses');
  checkEqual('the title', rev[0].title, 'a reordered header does not swap title for directory');
  checkEqual('F:/a/b', rev[0].directory, 'a reordered header does not swap directory for title');

  // Rejections, and the values that must survive them.
  checkEqual(0, parseCatalog('| # | updated | msgs | agent | directory | session id |').length,
    'a header missing the title column yields nothing');
  checkEqual(0, parseCatalog('not a table at all').length,
    'a document with no table yields nothing');

  const blank = [
    '| # | created | updated | msgs | agent | directory | title | session id |',
    '| --- | --- | --- | --- | --- | --- | --- | --- |',
    '| 1 |  | 2026-10-01 17:39 | 1 | build | `F:/a` | t | `ses_blank` |',
  ].join('\n');
  const b = parseCatalog(blank);
  checkEqual(1, b.length, 'an empty created cell does not drop the row');
  checkEqual('', b[0].created, 'an empty created cell reads as an empty string');

  const shortRow = [
    '| # | created | updated | msgs | agent | directory | title | session id |',
    '| --- | --- | --- | --- | --- | --- | --- | --- |',
    '| 1 | 2026-08-19 11:03 |',
    '| 2 | 2026-07-02 08:00 | 2026-01-01 09:00 | 7 | plan | `C:/x` | t | `ses_two` |',
  ].join('\n');
  const sr = parseCatalog(shortRow);
  checkEqual(1, sr.length, 'a row that lost its cells is skipped, and the rest still parse');
  checkEqual('ses_two', sr[0].id, 'the intact row is the one that survives');
}

// The generator's own guard against a junk timestamp is Number.isNaN on the
// constructed Date, but `new Date(null)` is epoch zero, which is a perfectly
// valid date. session_v2.time_created is nullable, so a row with no creation
// time would be written as 1970-01-01 and the C# side would display it
// cheerfully. Nothing may be written instead.
{
  const dir = scratch();
  const previous = process.env.SESSIONLAUNCHER_CATALOG;
  try {
    const markdownPath = join(dir, 'TOP-LEVEL-SESSIONS.md');
    process.env.SESSIONLAUNCHER_CATALOG = markdownPath;

    const dbdir = join(dir, 'db');
    mkdirSync(dbdir, { recursive: true });
    const db = new DatabaseSync(join(dbdir, 'opencode.db'));
    db.exec(`
      CREATE TABLE session_v2 (
        id TEXT PRIMARY KEY, title TEXT, directory TEXT, agent TEXT,
        time_created INTEGER, time_updated INTEGER, parent_id TEXT);
      CREATE TABLE session_message (session_id TEXT);
    `);
    const ins = db.prepare(
      `INSERT INTO session_v2 (id,title,directory,agent,time_created,time_updated,parent_id)
       VALUES (?,?,?,?,?,?,NULL)`);
    ins.run('ses_ok', 'ok', 'F:/a', 'build', 1787136180000, 1788387540000);
    ins.run('ses_null', 'no created', 'F:/a', 'build', null, 1788387540000);
    ins.run('ses_junk', 'junk created', 'F:/a', 'build', 'not-a-number', 1788387540000);
    db.close();

    generate({ dbPath: join(dbdir, 'opencode.db') });

    const text = readFileSync(markdownPath, 'utf8');
    check(!text.includes('1970-01-01'),
      'a NULL or unparseable time_created is written as an empty cell, not as 1970');
    check(!text.includes('not-a-number'),
      'and the raw value is never copied into the table');

    const rows = text.split(/\r?\n/).filter((l) => l.startsWith('| ') && !l.startsWith('| ---'));
    const cells = (row) => row.replaceAll('\\|', '\u0000').split('|').slice(1, -1).map((c) => c.trim());
    const nullRow = rows.find((r) => r.includes('ses_null'));
    const junkRow = rows.find((r) => r.includes('ses_junk'));
    checkEqual('', cells(nullRow)[1], 'the NULL row leaves the created cell empty');
    checkEqual('', cells(junkRow)[1], 'the junk row leaves the created cell empty');
    checkEqual('2026-09-03 06:19', cells(nullRow)[2],
      'and its updated cell is still real, because that one parsed');
  } finally {
    if (previous === undefined) delete process.env.SESSIONLAUNCHER_CATALOG;
    else process.env.SESSIONLAUNCHER_CATALOG = previous;
    rmSync(dir, { recursive: true, force: true });
  }
}

console.log(`test-catalog OK  assertions=${checks}`);