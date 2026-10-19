#!/usr/bin/env node
// Reference session-capture hook for Claude Code and Codex (contract docs/06 §6.5, FR-AG-04).
// Usage: node athar-hook.mjs start|commit|flush <client>   client = claude-code | codex
// Config: ~/.athar/config.json {"url","token","capture_repos":[...],"capture_exclude":[...]} (ATHAR_URL/ATHAR_TOKEN override).
// Capture settings (T136, FR-AG-04): the opted-in repos and Pause live on the server (GET /api/v1/me/capture-settings), read
// at session start and cached in ~/.athar/settings.json for offline use. A repo is captured when capture isn't paused, it is
// in the server's repos or the local capture_repos, and it isn't in the local capture_exclude.
// One lock (~/.athar/hook.lock, owned by a live PID) serializes enqueue, state and flush. Capture is best effort: every pending
// batch is written atomically to a local outbox BEFORE sending and re-sent (same commit_id)
// on the next run or `flush`. Never blocks the agent: failures are logged to stderr and the hook exits 0.
import { readFileSync, existsSync, writeFileSync, mkdirSync, unlinkSync, readdirSync, renameSync, linkSync, rmdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";

export const MAX_BODY_BYTES = 2_000_000;   // whole serialized POST body (UTF-8), envelope included
export const MAX_RECORD_BYTES = 256_000;   // larger records are skipped and counted
const ENVELOPE_RESERVE = 1_024;            // commit_id, indexes, skipped, JSON punctuation
const SECRET_KEY = /^(password|passwd|pwd|secret|client_secret|token|access_token|refresh_token|api[_-]?key|authorization|cookie)$/i;
const SECRET_PATTERNS = [
  /\bathar_pat_[A-Za-z0-9_-]{16,}/g, /\bgh[pousr]_[A-Za-z0-9]{20,}/g, /\bsk-[A-Za-z0-9_-]{20,}/g, /\bAKIA[0-9A-Z]{16}\b/g,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----/g,
  /\b(?<k>password|passwd|secret|token|api[_-]?key)(?<sep>\s*[:=]\s*)[^\s"',;]+/gi,
];
// Pattern + key-name redaction is a limited detector: common token formats, key=value pairs and secret-named JSON keys.
const redactString = (s) => SECRET_PATTERNS.reduce((acc, re) => acc.replace(re, (...args) => {
  const g = args.at(-1); // named-groups object; only the key=value pattern defines groups
  return g && typeof g === "object" && g.k ? `${g.k}${g.sep}[REDACTED]` : "[REDACTED]";
}), s);
export const redactValue = (v, key = "") =>
  key && SECRET_KEY.test(key) && (typeof v === "string" || typeof v === "number") ? "[REDACTED]"
  : typeof v === "string" ? redactString(v)
  : Array.isArray(v) ? v.map((x) => redactValue(x))
  : v && typeof v === "object" ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, redactValue(x, k)]))
  : v;
/** The capture rule (I-1): pause wins, then server ∪ local repos, minus the local excludes. */
export function shouldCapture(remote, settings, cfg = {}) {
  if (!remote || settings?.paused) return false;
  const norm = (list) => (Array.isArray(list) ? list : []).map((r) => normRemote(String(r)));
  return [...norm(settings?.repos), ...norm(cfg.capture_repos)].includes(remote) && !norm(cfg.capture_exclude).includes(remote);
}

/**
 * The server's capture settings, refreshed when asked (session start) or when nothing is cached yet; otherwise, or when the
 * server can't be reached, the last answer cached at settingsPath. Null when there is neither (only local repos count then).
 */
export async function loadSettings(url, token, settingsPath, { refresh = false, fetchFn = fetch } = {}) {
  const cached = () => { try { return JSON.parse(readFileSync(settingsPath, "utf8")); } catch { return null; } };
  if (!refresh && existsSync(settingsPath)) return cached();
  try {
    const r = await fetchFn(`${url}/api/v1/me/capture-settings`, { headers: { authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(5_000) });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const s = await r.json();
    const settings = { repos: Array.isArray(s.repos) ? s.repos : [], paused: s.paused === true, fetched_at: new Date().toISOString() };
    writeAtomic(settingsPath, JSON.stringify(settings));
    return settings;
  } catch { return cached(); } // offline: the last list fetched
}

export const normRemote = (r) => r.trim().replace(/^git@([^:]+):/, "$1/").replace(/^https?:\/\//, "").replace(/\.git$/, "").toLowerCase();

// Parse JSONL into records, redacting; skips oversized/unparseable lines (an unparseable LAST line is a partial write).
export function parseRecords(text) {
  const lines = text.split("\n"); const records = []; let skipped = 0;
  lines.forEach((line, i) => {
    if (!line.trim()) return;
    if (Buffer.byteLength(line, "utf8") > MAX_RECORD_BYTES) { skipped++; return; }
    try { records.push({ index: i, data: redactValue(JSON.parse(line)) }); }
    catch { if (i !== lines.length - 1) skipped++; } // partial last line: wait for the next commit
  });
  return { records, skipped };
}

// ALL records after the acknowledged cursor, split into bodies that each fit MAX_BODY_BYTES once serialized.
export function planBatches(records, afterIndex, skipped = 0) {
  const budget = MAX_BODY_BYTES - ENVELOPE_RESERVE;
  const bodies = []; let cur = []; let bytes = 2; // "[]"
  const close = () => {
    if (!cur.length) return;
    bodies.push({ commit_id: randomUUID(), from_index: cur[0].index, to_index: cur.at(-1).index, skipped: bodies.length ? 0 : skipped, records: cur });
    cur = []; bytes = 2;
  };
  for (const r of records) {
    if (r.index <= afterIndex) continue;
    const b = Buffer.byteLength(JSON.stringify(r), "utf8") + 1;
    if (bytes + b > budget) close();
    cur.push(r); bytes += b;
  }
  close();
  return bodies;
}

// Exclusive lock file with stale-lock recovery (a killed hook must not block capture forever).
// --- one lock for the whole hook run (enqueue, state and flush), owned by a live process ---
const pidAlive = (pid) => { try { process.kill(pid, 0); return true; } catch (e) { return e.code === "EPERM"; } };
// Returns a release function, or null if the lock is busy.
// - The lock appears atomically WITH its content (temp file + hard link, which fails if the lock exists), so no reader ever
//   sees an empty or half-written lock. An unreadable lock is treated as busy, never recovered.
// - Dead-owner recovery is serialized by an exclusive recovery directory; only its holder may remove the dead lock.
// ponytail: a recovery dir is NEVER cleared automatically (time-based clearing could evict a live, paused recoverer).
//   If a hook is killed mid-recovery (microsecond window), capture pauses until `node athar-hook.mjs unlock <client>`
//   is run with no hook processes alive. Nothing is lost meanwhile: pending batches stay in the outbox.
export function acquireLock(path, { pid = process.pid, isAlive = pidAlive } = {}) {
  const nonce = randomUUID();
  const release = () => { try { if (JSON.parse(readFileSync(path, "utf8")).nonce === nonce) unlinkSync(path); } catch {} }; // only our own lock
  const tryCreate = () => {
    const tmp = `${path}.${nonce}.tmp`;
    writeFileSync(tmp, JSON.stringify({ pid, nonce }));
    try { linkSync(tmp, path); return true; } catch (e) { if (e.code === "EEXIST") return false; throw e; } finally { unlinkSync(tmp); }
  };
  if (tryCreate()) return release;
  let owner; try { owner = JSON.parse(readFileSync(path, "utf8")); } catch { return null; } // unreadable: busy
  if (isAlive(owner.pid)) return null;                                                      // live owner: never steal
  const rec = `${path}.recover`;
  try { mkdirSync(rec); } catch { return null; }                                            // someone (live or abandoned) is recovering: busy
  try {
    let cur; try { cur = JSON.parse(readFileSync(path, "utf8")); } catch { cur = null; }
    if (cur && cur.nonce === owner.nonce) unlinkSync(path);                                 // still the same dead lock
    return tryCreate() ? release : null;
  } finally { rmdirSync(rec); }
}
// Manual recovery (run only when no hook process is alive): clears an abandoned recovery dir, and the lock if its owner is dead.
export function manualUnlock(path, { isAlive = pidAlive, log = console.log } = {}) {
  let owner = null; try { owner = JSON.parse(readFileSync(path, "utf8")); } catch {}
  if (owner && isAlive(owner.pid)) { log(`athar-hook: lock held by live process ${owner.pid}; nothing changed`); return false; }
  try { rmdirSync(`${path}.recover`); } catch {}
  try { unlinkSync(path); } catch {}
  log("athar-hook: lock cleared");
  return true;
}
// Atomic publish: write a temp file, then rename over the target (readers never see a partial file).
export function writeAtomic(path, data) {
  const tmp = `${path}.${randomUUID()}.tmp`;
  writeFileSync(tmp, data); renameSync(tmp, path);
}
// Send outbox entries oldest-first; stop at the first network failure (entries stay for the next run).
// Malformed entries are quarantined so they can't block the queue.
export async function flushOutbox(outboxDir, sessDir, post) {
  mkdirSync(join(outboxDir, "bad"), { recursive: true });
  for (const f of readdirSync(outboxDir).filter((f) => f.endsWith(".json")).sort()) {
    const p = join(outboxDir, f); let c;
    try { c = JSON.parse(readFileSync(p, "utf8")); } catch { renameSync(p, join(outboxDir, "bad", f)); continue; }
    const ack = await post(`/sessions/${c.session_id}/commit`, c.body); // same commit_id => server no-op on repeat
    const sp = join(sessDir, c.state_file);
    if (existsSync(sp)) { const st = JSON.parse(readFileSync(sp, "utf8")); st.acked = Math.max(st.acked ?? -1, ack.acked_index); writeAtomic(sp, JSON.stringify(st)); }
    unlinkSync(p);
  }
}

async function main() {
  const [mode, client] = process.argv.slice(2);
  if (!["start", "commit", "flush", "unlock"].includes(mode) || !["claude-code", "codex"].includes(client)) return;
  if (mode === "unlock") return manualUnlock(join(homedir(), ".athar", "hook.lock"));
  const home = join(homedir(), ".athar"), outboxDir = join(home, "outbox"), sessDir = join(home, "sessions");
  mkdirSync(outboxDir, { recursive: true }); mkdirSync(sessDir, { recursive: true });
  const cfgPath = join(home, "config.json");
  const cfg = existsSync(cfgPath) ? JSON.parse(readFileSync(cfgPath, "utf8")) : {};
  const url = process.env.ATHAR_URL || cfg.url, token = process.env.ATHAR_TOKEN || cfg.token;
  if (!url || !token) return;
  const release = acquireLock(join(home, "hook.lock"));
  if (!release) return; // another live hook run is busy; it (or the next run) will flush everything
  try { await run(mode, client, cfg, url, token, outboxDir, sessDir); } finally { release(); }
}

async function run(mode, client, cfg, url, token, outboxDir, sessDir) {
  const post = async (path, body) => {
    const json = JSON.stringify(body);
    if (Buffer.byteLength(json, "utf8") > MAX_BODY_BYTES) throw new Error("body over cap"); // defensive; planBatches prevents it
    const r = await fetch(`${url}/api/v1${path}`, {
      method: "POST", signal: AbortSignal.timeout(15_000),
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json", "x-athar-client": client }, body: json,
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
  };
  const flush = () => flushOutbox(outboxDir, sessDir, post);

  if (mode === "flush") return flush();
  const payload = JSON.parse(readFileSync(0, "utf8") || "{}");
  const extId = payload.session_id; if (!extId) return flush();
  let remote = null;
  try { remote = normRemote(execFileSync("git", ["remote", "get-url", "origin"], { cwd: payload.cwd || process.cwd(), stdio: ["ignore", "pipe", "ignore"] }).toString()); } catch {}
  const settings = await loadSettings(url, token, join(outboxDir, "..", "settings.json"), { refresh: mode === "start" });
  if (!shouldCapture(remote, settings, cfg)) return flush(); // opt-in per repo, and Pause stops everything

  const stateName = `${client}-${extId.replace(/[^A-Za-z0-9_-]/g, "_")}.json`;
  const statePath = join(sessDir, stateName);
  const st = existsSync(statePath) ? JSON.parse(readFileSync(statePath, "utf8")) : { acked: -1, queued: -1 };
  if (!st.athar_session) { // start fires on startup/resume/clear/compact; the server is idempotent on external_session_id
    const started = await post("/sessions", { client, external_session_id: extId, repo_remote: remote });
    st.athar_session = started.session_id;
    writeAtomic(statePath, JSON.stringify(st));
    if (started.capture === false) return flush(); // paused on the server since the settings were cached: send nothing
  }
  if (mode === "commit" && payload.transcript_path && existsSync(payload.transcript_path)) {
    const { records, skipped } = parseRecords(readFileSync(payload.transcript_path, "utf8"));
    // persist EVERY remaining batch (atomically) before sending anything, so a killed process loses nothing
    for (const body of planBatches(records, Math.max(st.acked ?? -1, st.queued ?? -1), skipped)) {
      writeAtomic(join(outboxDir, `${Date.now()}-${body.commit_id}.json`), JSON.stringify({ session_id: st.athar_session, state_file: stateName, body }));
      st.queued = body.to_index;
    }
    writeAtomic(statePath, JSON.stringify(st));
  }
  return flush();
}

if (process.argv[1]?.endsWith("athar-hook.mjs")) main().catch((e) => console.error(`athar-hook: ${e.message}`));
