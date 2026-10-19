import { Server, type Document } from "@hocuspocus/server";
import { randomInt, randomUUID, timingSafeEqual } from "node:crypto";
import type { IncomingMessage, ServerResponse } from "node:http";
import * as Y from "yjs";
import { fromMarkdown, fromYjs, toMarkdown, toYjs, type PMNode } from "./markdown.js";

// Hocuspocus sidecar: single writer of each live document. Every permission decision is made by `web`
// through the internal collab API (docs/06 §6.4, docs/03 §4.1).
const port = Number(process.env.PORT ?? 1234);
const webUrl = required("WEB_URL");
const secret = required("COLLAB_SECRET");
const LEASE_MS = 30_000;
const MAX_PERSIST_ATTEMPTS = 5;
/** Origin of updates collab applies itself (store load, 409 merge). They are never attributed to a principal. */
const FROM_STORE = "athar:store";

type Level = "view" | "comment" | "edit";
type Grant = { user: string; credential: string | null; level: Level; lease: string };
/** `opId` is set on the direct connection a server-side operation uses. */
type Ctx = Grant & { timer?: NodeJS.Timeout; opId?: string };
type Batch = { batch_id: string; user: string; credential: string | null; op_ids: string[] };
/** Per loaded document. `epoch` is new on every load, so a live token from before a restart is always stale (FR-RT-06). */
/** `applied`: op ids persisted recently or applied since this load, so a re-sent op (a retry, a concurrent duplicate) applies once. */
/** `loaded`: the stored live token this load started from; it stays valid until the first update after the load (FR-RT-06). */
type Live = { revision: number; epoch: number; seq: number; batches: Batch[]; sealed: number; persisting: Promise<void>; applied: Set<string>; loaded: string };
/**
 * A server-side operation (docs/06 §6.4). `from`/`to` are base64 Yjs relative positions (`Y.encodeRelativePosition`).
 * An anchorComment without them (an agent's) finds `quote` in the page; `anchor_hint` is nearby text that picks between repeats.
 */
type Op = {
  op_id: string; kind: string; user: string; credential?: string | null; expected_live_token?: string;
  markdown?: string; json?: PMNode; thread_id?: string; from?: string; to?: string; quote?: string; anchor_hint?: string;
};

const live = new Map<string, Live>();
/** Per-document serial queue for server-side operations (FR-RT-06). */
const queues = new Map<string, Promise<unknown>>();

const server = new Server<Ctx>({
  port,
  name: "athar-collab",
  quiet: true,
  debounce: 2_000, // FR-RT-05: persist on a 2 s debounce with a 30 s maximum wait
  maxDebounce: 30_000,

  async onAuthenticate({ token, documentName, connectionConfig }) {
    const grant = await authorize(documentName, { ticket: token });
    // FR-RT-03: viewers and commenters are read-only; Hocuspocus drops their updates.
    connectionConfig.readOnly = grant.level !== "edit";
    return grant;
  },

  async connected({ connection, context, documentName }) {
    // Lease renewal: a revocation or downgrade takes effect within 30 s (FR-ID-08).
    context.timer = setInterval(async () => {
      try {
        const grant = await authorize(documentName, { lease: context.lease });
        context.lease = grant.lease;
        if (grant.level !== "edit") connection.readOnly = true;
      } catch {
        connection.close();
      }
    }, LEASE_MS);
  },

  async onDisconnect({ context }) {
    clearInterval(context.timer);
  },

  async onLoadDocument({ document, documentName }) {
    const res = await web(`/internal/collab/pages/${documentName}/state`);
    if (!res.ok) throw new Error(res.status === 404 ? "not_found" : `state ${res.status}`);
    const state = (await res.json()) as {
      ydoc: string | null; revision: number; op_ids?: string[]; live_epoch: number; live_seq: number; json?: PMNode | null; markdown?: string | null;
    };
    if (state.ydoc) Y.applyUpdate(document, b64(state.ydoc), FROM_STORE);
    else if (state.json?.content?.length || state.markdown) {
      // A page stored without Yjs state (seeded or imported): build the live document from its stored projection, so the
      // editor shows it and the first edit doesn't persist an empty page over it.
      const nodes = state.json?.content?.length ? state.json.content : (fromMarkdown(state.markdown!).content ?? []);
      document.transact(() => document.getXmlFragment("default").push(toYjs(nodes)), FROM_STORE);
    }
    live.set(documentName, { revision: state.revision, epoch: randomInt(2 ** 48 - 1), seq: 0, batches: [], sealed: 0, persisting: Promise.resolve(), applied: new Set(state.op_ids ?? []), loaded: `${state.live_epoch}:${state.live_seq}` });
  },

  async onChange({ documentName, transactionOrigin, context }) {
    const l = live.get(documentName);
    if (!l || transactionOrigin === FROM_STORE) return;
    // FR-RT-04: attribute on arrival. Consecutive updates from one principal share a batch until a persist seals it;
    // a server-side operation always gets its own batch carrying its op_id.
    l.seq++;
    const last = l.batches.at(-1);
    const coalesce = !context.opId && l.batches.length > l.sealed && last?.op_ids.length === 0 && last.user === context.user && last.credential === context.credential;
    if (coalesce) return;
    l.batches.push({ batch_id: randomUUID(), user: context.user, credential: context.credential ?? null, op_ids: context.opId ? [context.opId] : [] });
  },

  async onStoreDocument({ documentName, document }) {
    const l = live.get(documentName);
    if (l) await persistQueued(documentName, document, l);
  },

  // `POST /documents/{id}/ops` (M-B1), `GET /documents/{id}` (a live snapshot) and `POST /convert` (docs/06 §6.4).
  // Any other request gets Hocuspocus' default reply (the health check).
  async onRequest({ request, response }) {
    const id = request.method === "POST" && request.url?.match(/^\/documents\/([0-9a-f-]{36})\/ops$/)?.[1];
    const snapshotId = request.method === "GET" && request.url?.match(/^\/documents\/([0-9a-f-]{36})$/)?.[1];
    const convert = request.method === "POST" && request.url === "/convert";
    if (!id && !snapshotId && !convert) return;
    try {
      if (!secretMatches(request.headers["x-collab-secret"])) send(response, 401, { error: "unauthorized" });
      else if (convert) send(response, 200, convertBody(JSON.parse(await readBody(request))));
      else if (snapshotId) send(response, ...snapshot(snapshotId));
      else {
        const op = JSON.parse(await readBody(request)) as Op;
        const { status, body } = await enqueue(id as string, () => runOp(id as string, op));
        send(response, status, body);
      }
    } catch (e) {
      send(response, 500, { error: "internal", message: String(e) });
    }
    throw null; // handled: skip the default reply
  },

  async afterUnloadDocument({ documentName }) {
    live.delete(documentName);
  },
});

/** One persist at a time per document; a failure doesn't block the next one. */
function persistQueued(page: string, document: Document, l: Live) {
  const run = l.persisting.then(() => persist(page, document, l));
  l.persisting = run.catch(() => {});
  return run;
}

const liveToken = (l: Live) => `${l.epoch}:${l.seq}`;

/**
 * FR-RT-06: the current token always holds. The stored token (what `get_page` returned while the page was unloaded) also
 * holds until the first update after this load: the content is still exactly what was stored. After a restart, re-synced
 * or new edits move `seq`, so a token from the old process is refused.
 */
const tokenHolds = (l: Live, token: string) => token === liveToken(l) || (l.seq === 0 && token === l.loaded);

function enqueue<T>(page: string, fn: () => Promise<T>): Promise<T> {
  const run = (queues.get(page) ?? Promise.resolve()).then(fn);
  queues.set(page, run.catch(() => {}));
  return run;
}

const OP_KINDS = new Set(["create", "append", "replace", "restore", "anchorComment"]);

/**
 * Applies one server-side operation and persists it before answering, so `web` reports success only once the
 * revision holding the operation is committed (docs/03 §4.2). `web` records the op as durable first (T040).
 */
async function runOp(page: string, op: Op): Promise<{ status: number; body: unknown }> {
  if (!op.op_id || !op.user) return { status: 400, body: { error: "invalid_input", message: "op_id and user are required" } };
  if (!OP_KINDS.has(op.kind)) return { status: 400, body: { error: "unsupported_op", message: `${op.kind} isn't an operation` } };
  let conn;
  try {
    conn = await server.hocuspocus.openDirectConnection(page, { user: op.user, credential: op.credential ?? null, opId: op.op_id } as Ctx);
  } catch (e) {
    if (e instanceof Error && e.message === "not_found") return { status: 404, body: { error: "not_found" } };
    throw e;
  }
  try {
    const l = live.get(page)!;
    const fragment = conn.document!.getXmlFragment("default");
    let orphaned: string[] = [];
    // An op is applied once (FR-RT-07): `applied` holds the ops persisted recently (from web, on load) and the ones applied
    // since. An op applied but never persisted died with its process, so applying it anew is right.
    if (!l.applied.has(op.op_id)) {
      // Checking and applying happen in one tick, so no browser update can land in between (FR-RT-06).
      if (op.expected_live_token != null && !tokenHolds(l, op.expected_live_token))
        return { status: 409, body: { error: "live_token_moved", live_token: liveToken(l) } };
      if (op.kind === "replace" && op.expected_live_token == null)
        return { status: 400, body: { error: "invalid_input", message: "replace needs expected_live_token" } };
      const apply = prepare(op, conn.document!);
      if ("error" in apply) return { status: 422, body: apply };
      const before = anchors(fragment);
      l.applied.add(op.op_id);
      await conn.transact(apply.change);
      // Anchors whose text went away: the replaced range takes them with it (docs/03 §4.2).
      const after = anchors(fragment);
      orphaned = [...before].filter((t) => !after.has(t));
    }
    await persistQueued(page, conn.document!, l);
    return { status: 200, body: { op_id: op.op_id, live_token: liveToken(l), persisted_revision: l.revision, orphaned_threads: orphaned } };
  } finally {
    await conn.disconnect();
  }
}

/** Validates an op against the live document and returns the change to apply, or why it can't be applied. */
function prepare(op: Op, doc: Y.Doc): { change: (doc: Y.Doc) => void } | { error: string; message: string } {
  const fragment = doc.getXmlFragment("default");
  switch (op.kind) {
    case "create":
    case "append":
      return { change: () => fragment.push(toYjs(fromMarkdown(op.markdown ?? "").content ?? [])) };
    case "replace":
      return { change: () => { fragment.delete(0, fragment.length); fragment.push(toYjs(fromMarkdown(op.markdown ?? "").content ?? [])); } };
    case "restore":
      // FR-PG-05: the version's content replaces the live content; the forced persist makes it current_revision + 1.
      if (!op.json) return { error: "invalid_input", message: "restore needs the version's json" };
      return { change: () => { fragment.delete(0, fragment.length); fragment.push(toYjs(op.json!.content ?? [])); } };
    case "anchorComment": {
      // FR-CM-01: a non-inclusive `commentAnchor` mark over the quoted text, found through relative positions or, for agents,
      // by searching for the quote. ponytail: the anchor must sit inside one text run (one block); cross-block quotes fail.
      if (!op.thread_id || !op.quote) return { error: "anchor_failed", message: "The selection can't be found in the page." };
      const found = op.from || op.to ? byPositions(doc, op) : byQuote(fragment, op.quote, op.anchor_hint);
      if ("error" in found) return found;
      const { target, index } = found, length = op.quote.length;
      return { change: () => target.format(index, length, { commentAnchor: { threadId: op.thread_id } }) };
    }
    default:
      return { error: "unsupported_op", message: op.kind };
  }
}

type Spot = { target: Y.XmlText; index: number } | { error: string; message: string };

/** A text run as a string whose indexes are Yjs indexes: an inline node (mention, hard break) is one character. */
const runText = (t: Y.XmlText) =>
  (t.toDelta() as { insert: unknown }[]).map((d) => (typeof d.insert === "string" ? d.insert : "￼")).join("");

/** The browser's selection, as relative positions, still holding the quoted text. */
function byPositions(doc: Y.Doc, op: Op): Spot {
  const at = (p?: string) => (p ? Y.createAbsolutePositionFromRelativePosition(Y.decodeRelativePosition(b64(p)), doc) : null);
  const from = at(op.from), to = at(op.to);
  if (!from || !to || from.type !== to.type || !(from.type instanceof Y.XmlText) || to.index <= from.index)
    return { error: "anchor_failed", message: "The selection can't be found in the page." };
  if (runText(from.type).slice(from.index, to.index) !== op.quote)
    return { error: "anchor_failed", message: "The quoted text has changed." };
  return { target: from.type, index: from.index };
}

/** The one place the quote appears; with repeats, the one whose block also holds `hint`. */
function byQuote(fragment: Y.XmlFragment, quote: string, hint?: string): Spot {
  const hits: { target: Y.XmlText; index: number; text: string }[] = [];
  const walk = (node: Y.XmlFragment | Y.XmlElement) => {
    for (const child of node.toArray()) {
      if (child instanceof Y.XmlElement) walk(child);
      else if (child instanceof Y.XmlText) {
        const text = runText(child);
        for (let i = text.indexOf(quote); i >= 0; i = text.indexOf(quote, i + 1)) hits.push({ target: child, index: i, text });
      }
    }
  };
  walk(fragment);
  const picked = hits.length > 1 && hint ? hits.filter((h) => h.text.includes(hint)) : hits;
  if (picked.length === 1) return picked[0];
  return picked.length === 0 && hits.length === 0
    ? { error: "anchor_failed", message: "The quoted text isn't in the page (a quote can't span blocks)." }
    : { error: "anchor_failed", message: `The quote appears ${hits.length} times; pass anchor_hint with nearby text from its paragraph.` };
}

/** Thread ids of every comment anchor in the document. */
function anchors(node: Y.XmlFragment | Y.XmlElement): Set<string> {
  const found = new Set<string>();
  for (const child of node.toArray()) {
    if (child instanceof Y.XmlText) {
      for (const d of child.toDelta() as { attributes?: { commentAnchor?: { threadId?: string } } }[])
        if (d.attributes?.commentAnchor?.threadId) found.add(d.attributes.commentAnchor.threadId);
    } else if (child instanceof Y.XmlElement) for (const t of anchors(child)) found.add(t);
  }
  return found;
}

/**
 * FR-RT-06: content and live token from one snapshot, when the document is loaded here. Otherwise web answers from the
 * database with the stored epoch.
 */
function snapshot(page: string): [number, unknown] {
  const doc = server.hocuspocus.documents.get(page);
  const l = live.get(page);
  if (!doc || !l) return [404, { error: "not_loaded" }];
  const json: PMNode = { type: "doc", content: fromYjs(doc.getXmlFragment("default")) };
  return [200, { json, markdown: toMarkdown(json), live_token: liveToken(l), revision: l.revision }];
}

/** `{markdown}` → `{json}`, or `{json}` → `{markdown}` (the shared schema, docs/06 §6.7). */
function convertBody(body: { markdown?: string; json?: PMNode }) {
  return typeof body.markdown === "string" ? { json: fromMarkdown(body.markdown) } : { markdown: toMarkdown(body.json ?? { type: "doc" }) };
}

function secretMatches(given: string | string[] | undefined) {
  const a = Buffer.from(typeof given === "string" ? given : ""), b = Buffer.from(secret);
  return a.length === b.length && timingSafeEqual(a, b);
}

async function readBody(req: IncomingMessage) {
  let body = "";
  for await (const chunk of req) body += chunk;
  return body;
}

function send(res: ServerResponse, status: number, body: unknown) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

async function persist(page: string, document: Document, l: Live) {
  for (let attempt = 1; attempt <= MAX_PERSIST_ATTEMPTS; attempt++) {
    const n = l.batches.length;
    if (n === 0) return; // nothing attributed since the last revision
    l.sealed = n;
    const json: PMNode = { type: "doc", content: fromYjs(document.getXmlFragment("default")) };
    const batches = l.batches.slice(0, n);
    const res = await web(`/internal/collab/pages/${page}`, {
      method: "PUT",
      body: JSON.stringify({
        ydoc: Buffer.from(Y.encodeStateAsUpdate(document)).toString("base64"),
        json,
        markdown: toMarkdown(json),
        base_revision: l.revision,
        live_epoch: l.epoch,
        live_seq: l.seq,
        batches,
        op_ids: batches.flatMap((b) => b.op_ids),
      }),
    });
    if (res.ok) {
      l.revision = ((await res.json()) as { revision: number }).revision;
      l.batches.splice(0, n);
      l.sealed = 0;
      document.broadcastStateless(JSON.stringify({ saved: l.revision }));
      return;
    }
    if (res.status !== 409) throw new Error(`persist ${page}: ${res.status}`);
    // Stale store: CRDT-merge the stored state into the live document and retry on the new base.
    // Pending updates and their batches stay where they are (docs/03 §4.1).
    const stored = (await res.json()) as { ydoc: string; revision: number };
    Y.applyUpdate(document, b64(stored.ydoc), FROM_STORE);
    l.revision = stored.revision;
  }
  throw new Error(`persist ${page}: still stale after ${MAX_PERSIST_ATTEMPTS} attempts`);
}

async function authorize(page: string, body: { ticket?: string; lease?: string }): Promise<Grant> {
  const res = await web("/internal/collab/authorize", { method: "POST", body: JSON.stringify({ ...body, page }) });
  if (!res.ok) throw new Error(`authorize: ${res.status}`);
  return (await res.json()) as Grant;
}

function web(path: string, init: RequestInit = {}) {
  return fetch(webUrl + path, { ...init, headers: { "content-type": "application/json", "x-collab-secret": secret } });
}

function b64(s: string) {
  return new Uint8Array(Buffer.from(s, "base64"));
}

function required(name: string) {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is not set`);
  return v;
}

server.listen().then(() => console.log(`collab listening on ws://localhost:${port}`));
