// ponytail: one runnable check for the hook's non-trivial logic. Run: node athar-hook.test.mjs
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, readdirSync, existsSync, rmSync, unlinkSync, rmdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { redactValue, parseRecords, planBatches, normRemote, MAX_BODY_BYTES, acquireLock, manualUnlock, writeAtomic, flushOutbox, shouldCapture, loadSettings } from "./athar-hook.mjs";

// redaction inside JSON keeps the record valid; secret-named keys are redacted whatever their value looks like
const { records: r1 } = parseRecords('{"text":"password=hunter2 and ghp_abcdefghijklmnopqrstuvwxyz123"}\n');
assert.equal(r1[0].data.text, "password=[REDACTED] and [REDACTED]");
assert.deepEqual(redactValue({ password: "hunter2", token: "ordinary-secret", nested: { api_key: 42 }, note: "fine" }),
  { password: "[REDACTED]", token: "[REDACTED]", nested: { api_key: "[REDACTED]" }, note: "fine" });

// oversized record skipped and counted; earlier records survive; partial last line deferred (not counted)
const big = JSON.stringify({ t: "x".repeat(300_000) });
const { records: r2, skipped } = parseRecords(`{"i":0}\n${big}\n{"i":2}\n{"i":3, "partial`);
assert.deepEqual(r2.map((r) => r.data.i), [0, 2]);
assert.equal(skipped, 1);

// every remaining record is planned (nothing left behind), each serialized body fits the cap, Arabic counted in bytes
const lines = Array.from({ length: 12 }, (_, i) => JSON.stringify({ i, t: "مرحبا ".repeat(16_000) })).join("\n"); // ~200 KB each
const { records: r3 } = parseRecords(lines + "\n");
const bodies = planBatches(r3, -1);
assert.equal(bodies.flatMap((b) => b.records).length, r3.length, "all records planned");
for (const b of bodies) assert.ok(Buffer.byteLength(JSON.stringify(b), "utf8") <= MAX_BODY_BYTES, "body under cap");
assert.ok(bodies.length > 1);
assert.equal(planBatches(r3, r3.at(-1).index).length, 0, "nothing after the cursor");

// lock: a live owner is never stolen; a dead owner is recovered; release removes only our own lock
const tmp = mkdtempSync(join(tmpdir(), "athar-hook-"));
const lockPath = join(tmp, "hook.lock");
const releaseA = acquireLock(lockPath, { pid: 111, isAlive: () => true });
assert.ok(releaseA);
assert.equal(acquireLock(lockPath, { pid: 222, isAlive: () => true }), null, "active lock not stolen, however long it runs");
const releaseB = acquireLock(lockPath, { pid: 222, isAlive: (p) => p !== 111 }); // 111 has died
assert.ok(releaseB, "dead owner's lock recovered");
releaseA(); // stale release from the dead owner must not remove B's lock
assert.ok(existsSync(lockPath), "release removes only the caller's own lock");
releaseB(); assert.ok(!existsSync(lockPath));
// F68 interleaving: a lock that is empty or half-written (owner still initializing) is BUSY, never recovered
writeFileSync(lockPath, "");
assert.equal(acquireLock(lockPath, { pid: 333, isAlive: () => false }), null, "unreadable lock treated as busy");
unlinkSync(lockPath);
// recovery is serialized: while another process holds the recovery dir, a dead lock is not touched
const releaseDead = acquireLock(lockPath, { pid: 444, isAlive: () => true });
mkdirSync(lockPath + ".recover");
assert.equal(acquireLock(lockPath, { pid: 555, isAlive: (p) => p !== 444 }), null, "concurrent recoverer backs off");
assert.ok(existsSync(lockPath));
rmdirSync(lockPath + ".recover");
// cycle-6 interleaving: a recovery dir is never stolen, however old (a paused live recoverer keeps exclusivity)
mkdirSync(lockPath + ".recover");
assert.equal(acquireLock(lockPath, { pid: 666, isAlive: (p) => p !== 444 }), null, "abandoned or paused recovery stays busy");
assert.equal(acquireLock(lockPath, { pid: 777, isAlive: (p) => p !== 444 }), null, "and stays busy for every caller");
// manual unlock refuses while the lock owner is alive, clears once it is dead
assert.equal(manualUnlock(lockPath, { isAlive: () => true, log: () => {} }), false);
assert.ok(existsSync(lockPath));
assert.equal(manualUnlock(lockPath, { isAlive: () => false, log: () => {} }), true);
assert.ok(!existsSync(lockPath) && !existsSync(lockPath + ".recover"));
// normal dead-owner recovery: exactly one recoverer, then the dead owner's late release is harmless
const releaseOld = acquireLock(lockPath, { pid: 888, isAlive: () => true });
const releaseC = acquireLock(lockPath, { pid: 999, isAlive: (p) => p !== 888 });
assert.ok(releaseC, "dead owner recovered");
assert.ok(!existsSync(lockPath + ".recover"), "recovery dir released after recovery");
releaseOld(); releaseDead(); assert.ok(existsSync(lockPath), "stale releases don't remove the new lock");
releaseC();

// atomic writes leave no temp files; flush sends good entries in order, quarantines malformed ones, updates acked
const outbox = join(tmp, "outbox"), sess = join(tmp, "sessions"); mkdirSync(outbox); mkdirSync(sess);
writeAtomic(join(sess, "s.json"), JSON.stringify({ acked: -1 }));
writeAtomic(join(outbox, "1-a.json"), JSON.stringify({ session_id: "S", state_file: "s.json", body: { commit_id: "a" } }));
writeFileSync(join(outbox, "2-b.json"), '{"session_id": "S", "body": '); // interrupted legacy write
writeAtomic(join(outbox, "3-c.json"), JSON.stringify({ session_id: "S", state_file: "s.json", body: { commit_id: "c" } }));
const sent = [];
await flushOutbox(outbox, sess, async (_p, body) => { sent.push(body.commit_id); return { acked_index: sent.length * 10 }; });
assert.deepEqual(sent, ["a", "c"]);
assert.deepEqual(readdirSync(outbox).filter((f) => f.endsWith(".json")), []);
assert.ok(existsSync(join(outbox, "bad", "2-b.json")), "malformed entry quarantined");
assert.equal(JSON.parse(readFileSync(join(sess, "s.json"), "utf8")).acked, 20);
assert.equal(readdirSync(sess).filter((f) => f.endsWith(".tmp")).length, 0);
// a network failure stops the flush and keeps the entry for the next run
writeAtomic(join(outbox, "4-d.json"), JSON.stringify({ session_id: "S", state_file: "s.json", body: { commit_id: "d" } }));
await assert.rejects(flushOutbox(outbox, sess, async () => { throw new Error("offline"); }));
assert.ok(existsSync(join(outbox, "4-d.json")));
rmSync(tmp, { recursive: true, force: true });

assert.equal(normRemote("git@github.com:Org/Repo.git"), "github.com/org/repo");
assert.equal(normRemote("https://github.com/org/repo\n"), "github.com/org/repo");
// T136 capture rule: Pause wins; server repos and local capture_repos both opt in; local capture_exclude opts out
const repo = "github.com/org/repo";
assert.equal(shouldCapture(repo, { repos: ["git@github.com:Org/Repo.git"], paused: false }, {}), true, "server list, normalized");
assert.equal(shouldCapture(repo, null, { capture_repos: ["https://github.com/org/repo"] }), true, "local list, offline with no cache");
assert.equal(shouldCapture(repo, { repos: [repo], paused: true }, { capture_repos: [repo] }), false, "pause stops everything");
assert.equal(shouldCapture(repo, { repos: [repo] }, { capture_exclude: ["github.com/Org/Repo"] }), false, "local exclude");
assert.equal(shouldCapture("github.com/other/x", { repos: [repo] }, {}), false, "not opted in");
assert.equal(shouldCapture(null, { repos: [repo] }, {}), false, "no remote");

// settings: refreshed at start and cached; offline falls back to the cache; a later run reads the cache without fetching
const sdir = mkdtempSync(join(tmpdir(), "athar-settings-")), spath = join(sdir, "settings.json");
let calls = 0;
const ok = async (u, o) => { calls++; assert.equal(o.headers.authorization, "Bearer T"); assert.ok(u.endsWith("/api/v1/me/capture-settings"));
  return { ok: true, json: async () => ({ repos: [repo], paused: false, auto_accept: { personal: true, shared: false } }) }; };
const down = async () => { calls++; throw new Error("offline"); };
assert.equal(await loadSettings("https://a", "T", spath, { refresh: true, fetchFn: down }), null, "nothing cached yet");
assert.deepEqual((await loadSettings("https://a", "T", spath, { refresh: true, fetchFn: ok })).repos, [repo]);
assert.deepEqual((await loadSettings("https://a", "T", spath, { refresh: true, fetchFn: down })).repos, [repo], "offline: last list");
const before = calls;
assert.equal((await loadSettings("https://a", "T", spath, { fetchFn: ok })).paused, false);
assert.equal(calls, before, "commit runs read the cache");
rmSync(sdir, { recursive: true, force: true });
console.log("athar-hook self-test ok");
