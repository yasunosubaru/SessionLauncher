// server.mjs
//
// SessionLauncher MCP server — JSON-RPC 2.0 over stdio, implemented by hand.
// ZERO npm dependencies (no @modelcontextprotocol/sdk, no zod).
//
// Protocol channel rules:
//   - stdin  : line-delimited JSON, one JSON-RPC message per line.
//   - stdout : line-delimited JSON, one JSON-RPC RESPONSE per line. Nothing
//              else may ever be written to stdout.
//   - stderr : all human-readable logging.
//
// Run: node server.mjs

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

import { loadCatalog } from './lib/catalog.mjs';

const SERVER_NAME = 'sessionlauncher-mcp';
const SERVER_VERSION = '1.0.0';
const DEFAULT_PROTOCOL_VERSION = '2024-11-05';
const DEFAULT_LIMIT = 50;

/** A tool-level failure that should be reported as `isError`, not a crash. */
class ToolError extends Error {}

// ---------------------------------------------------------------------------
// Logging (stderr only)
// ---------------------------------------------------------------------------
function log(...args) {
  process.stderr.write(`[${SERVER_NAME}] ${args.join(' ')}\n`);
}

// ---------------------------------------------------------------------------
// Tool implementations
// ---------------------------------------------------------------------------

/**
 * list_sessions({ query?, limit? }) -> JSON array of
 * {id,title,directory,agent,updated,msgs}. `query` is a case-insensitive
 * substring over title/id/directory. `limit` defaults to 50.
 */
function listSessions(args = {}) {
  const sessions = loadCatalog();
  const query = typeof args.query === 'string' ? args.query.toLowerCase() : '';
  const filtered = query
    ? sessions.filter((s) =>
        [s.title, s.id, s.directory].some((field) =>
          String(field ?? '').toLowerCase().includes(query),
        ),
      )
    : sessions;

  const limit =
    typeof args.limit === 'number' && Number.isFinite(args.limit) && args.limit > 0
      ? Math.floor(args.limit)
      : DEFAULT_LIMIT;

  return JSON.stringify(filtered.slice(0, limit), null, 2);
}

/**
 * open_session({ sessionId, target }) -> launch a session in a UI.
 *   target 'openchamber' : detached deep link
 *   target 'opencode'    : detached `opencode -s <id>` (with OPENCODE_CONFIG stripped)
 * Validates sessionId against the catalog first.
 */
function openSession(args = {}) {
  const sessionId = args.sessionId;
  const target = args.target;

  if (typeof sessionId !== 'string' || sessionId.length === 0) {
    throw new ToolError('open_session requires a non-empty string `sessionId`.');
  }

  const sessions = loadCatalog();
  if (!sessions.some((s) => s.id === sessionId)) {
    throw new ToolError(
      `Unknown session id: ${sessionId}. Run list_sessions (or refresh_catalog) to see valid ids.`,
    );
  }

  if (target === 'openchamber') {
    const exe =
      process.env.SESSIONLAUNCHER_OPENCHAMBER_EXE ||
      'C:\\Users\\demo\\AppData\\Local\\Programs\\@openchamberelectron\\OpenChamber.exe';
    spawnDetached(exe, [`openchamber://session/${sessionId}`], process.env, `OpenChamber (${exe})`);
    return `Launched OpenChamber for session ${sessionId}.`;
  }

  if (target === 'opencode') {
    // The child MUST NOT inherit OPENCODE_CONFIG: it points at a config with an
    // unsupported `plugins` key and every opencode invocation aborts while set.
    const env = { ...process.env };
    delete env.OPENCODE_CONFIG;
    spawnDetached('opencode', ['-s', sessionId], env, 'opencode');
    return `Launched opencode for session ${sessionId}.`;
  }

  throw new ToolError(
    `Unknown target: ${String(target)}. Expected 'openchamber' or 'opencode'.`,
  );
}

/** Spawn a detached, hidden process. Spawn failures are logged, not thrown. */
function spawnDetached(command, argv, env, label) {
  const child = spawn(command, argv, {
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
    env,
  });
  // Detached children still emit 'error' asynchronously (e.g. ENOENT). Without
  // a listener that would be an uncaught exception and take down the server.
  child.on('error', (err) => log(`spawn ${label} failed: ${err.message}`));
  child.unref();
}

/**
 * refresh_catalog({}) -> regenerate from the SQLite DB, report count + path.
 * The generator is imported lazily so `node:sqlite` (and its experimental
 * warning) only loads when this tool is actually used.
 */
async function refreshCatalogTool() {
  const { generate } = await import('./refresh_catalog.mjs');
  const result = generate();
  return JSON.stringify(
    { count: result.count, path: result.markdownPath, sessionsJson: result.jsonPath },
    null,
    2,
  );
}

// ---------------------------------------------------------------------------
// Tool registry
// ---------------------------------------------------------------------------
const TOOLS = [
  {
    name: 'list_sessions',
    description:
      'List top-level opencode sessions from the catalog. Optional case-insensitive substring query over title/id/directory, and a limit (default 50).',
    inputSchema: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Case-insensitive substring over title/id/directory.' },
        limit: { type: 'number', description: 'Maximum rows to return (default 50).' },
      },
      additionalProperties: false,
    },
    handler: (args) => listSessions(args),
  },
  {
    name: 'open_session',
    description:
      "Open a catalog session in 'openchamber' (deep link) or 'opencode' (opencode -s <id>).",
    inputSchema: {
      type: 'object',
      properties: {
        sessionId: { type: 'string', description: 'Session id from list_sessions.' },
        target: { type: 'string', enum: ['openchamber', 'opencode'] },
      },
      required: ['sessionId', 'target'],
      additionalProperties: false,
    },
    handler: (args) => openSession(args),
  },
  {
    name: 'refresh_catalog',
    description:
      'Regenerate the catalog markdown + sessions.json from the opencode database.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    handler: () => refreshCatalogTool(),
  },
];

const TOOLS_BY_NAME = new Map(TOOLS.map((t) => [t.name, t]));

// ---------------------------------------------------------------------------
// JSON-RPC plumbing
// ---------------------------------------------------------------------------
function writeMessage(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function writeResult(id, result) {
  writeMessage({ jsonrpc: '2.0', id, result });
}

function writeError(id, code, message, data) {
  const error = { code, message };
  if (data !== undefined) error.data = data;
  writeMessage({ jsonrpc: '2.0', id, error });
}

function textResult(text) {
  return { content: [{ type: 'text', text }], isError: false };
}

function errorResult(text) {
  return { content: [{ type: 'text', text }], isError: true };
}

function handleInitialize(params = {}) {
  const requested = typeof params.protocolVersion === 'string' ? params.protocolVersion : null;
  return {
    protocolVersion: requested || DEFAULT_PROTOCOL_VERSION,
    capabilities: { tools: {} },
    serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
  };
}

async function handleToolsCall(params = {}) {
  const name = params.name;
  const tool = TOOLS_BY_NAME.get(name);
  if (!tool) {
    return { rpcError: { code: -32602, message: `Unknown tool: ${String(name)}` } };
  }
  const args = params.arguments && typeof params.arguments === 'object' ? params.arguments : {};
  try {
    const text = await tool.handler(args);
    return { result: textResult(typeof text === 'string' ? text : JSON.stringify(text)) };
  } catch (err) {
    if (err instanceof ToolError) {
      return { result: errorResult(err.message) };
    }
    log(`tool ${name} failed: ${err.stack || err.message}`);
    return { result: errorResult(`${name} failed: ${err.message}`) };
  }
}

/**
 * Handle one parsed JSON-RPC message. Notifications (no id) never produce a
 * response and resolve to undefined.
 * @returns {Promise<object|undefined>}
 */
async function handleMessage(msg) {
  const id = Object.prototype.hasOwnProperty.call(msg, 'id') ? msg.id : undefined;
  const isNotification = id === undefined || id === null;

  const method = msg.method;
  if (typeof method !== 'string') {
    if (isNotification) return undefined;
    return { id: id ?? null, error: { code: -32600, message: 'Invalid Request: missing method' } };
  }

  switch (method) {
    case 'initialize':
      if (isNotification) return undefined;
      return { id, result: handleInitialize(msg.params) };
    case 'ping':
      if (isNotification) return undefined;
      return { id, result: {} };
    case 'tools/list':
      if (isNotification) return undefined;
      return {
        id,
        result: {
          tools: TOOLS.map((t) => ({
            name: t.name,
            description: t.description,
            inputSchema: t.inputSchema,
          })),
        },
      };
    case 'tools/call': {
      if (isNotification) return undefined;
      const { result, rpcError } = await handleToolsCall(msg.params);
      if (rpcError) return { id, error: rpcError };
      return { id, result };
    }
    default:
      // Notifications such as 'notifications/initialized' are silent.
      if (isNotification) return undefined;
      return { id, error: { code: -32601, message: `Method not found: ${method}` } };
  }
}

// ---------------------------------------------------------------------------
// stdin loop
// ---------------------------------------------------------------------------
const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

/**
 * Requests whose handler has not settled yet.
 *
 * Needed because tool handlers are async: when stdin reaches EOF the 'close'
 * event fires synchronously, and calling process.exit() there would kill any
 * in-flight tool call (spawning OpenChamber, querying SQLite) before it could
 * write its response. We let those finish, then exit on our own.
 */
let inFlight = 0;

rl.on('line', (line) => {
  const trimmed = line.trim();
  if (trimmed === '') return;
  let msg;
  try {
    msg = JSON.parse(trimmed);
  } catch (err) {
    writeError(null, -32700, 'Parse error');
    return;
  }
  inFlight += 1;
  handleMessage(msg)
    .then((response) => {
      if (response === undefined) return;
      if (response.error) {
        writeError(response.id, response.error.code, response.error.message, response.error.data);
      } else {
        writeResult(response.id, response.result);
      }
    })
    .catch((err) => {
      log(`unhandled error: ${err.stack || err.message}`);
      const id = Object.prototype.hasOwnProperty.call(msg, 'id') ? msg['id'] : null;
      writeError(id, -32603, 'Internal error');
    })
    .finally(() => {
      inFlight -= 1;
      maybeExit();
    });
});

/** Exit once stdin is closed and nothing is still running. */
let stdinClosed = false;
function maybeExit() {
  if (stdinClosed && inFlight === 0) {
    log('stdin closed and no work in flight, exiting');
    process.exit(0);
  }
}

rl.on('close', () => {
  stdinClosed = true;
  // With no further input and no pending work, node exits on its own; this only
  // covers the case where stdin closed *while* requests were still running.
  maybeExit();
});

log(`ready (protocol ${DEFAULT_PROTOCOL_VERSION}), tools: ${TOOLS.map((t) => t.name).join(', ')}`);
