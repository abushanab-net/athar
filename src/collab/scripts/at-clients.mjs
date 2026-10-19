// Real Yjs clients for the collab acceptance tests, driven by tests/Athar.IntegrationTests/CollabTests.cs.
// Usage: node scripts/at-clients.mjs <at10|at11|at12-merge|at12-restart|ops|markdown>. The environment names the endpoints
// (WEB_URL, COLLAB_URL, COLLAB_HTTP, COLLAB_SECRET), the page (PAGE) and qa seed principals (EDITOR, VIEWER,
// AGENT_USER, AGENT_CREDENTIAL). Prints "ok" and exits 0 when the scenario holds.
// at12-restart prints RESTART and waits for a line on stdin.
import { HocuspocusProvider } from "@hocuspocus/provider";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { createInterface } from "node:readline";
import * as Y from "yjs";

const { WEB_URL, COLLAB_URL, COLLAB_HTTP, COLLAB_SECRET, PAGE: page, EDITOR, VIEWER, AGENT_USER, AGENT_CREDENTIAL } = process.env;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function assert(cond, msg) {
  if (!cond) throw new Error(`assertion failed: ${msg}`);
}

async function call(url, init = {}) {
  const res = await fetch(url, { ...init, headers: { "content-type": "application/json", "x-collab-secret": COLLAB_SECRET, ...init.headers } });
  return { status: res.status, body: await res.json().catch(() => null) };
}

async function web(path, init = {}) {
  const { status, body } = await call(WEB_URL + path, init);
  if (status >= 400) throw new Error(`${init.method ?? "GET"} ${path}: ${status}`);
  return body;
}

const ticket = async (user, level, credential) =>
  (await web("/internal/collab/tickets", { method: "POST", body: JSON.stringify({ user, credential, page, level }) })).ticket;

/** A browser-like client. Each (re)connect fetches a fresh single-use ticket. */
function client(user, level, credential = null) {
  const doc = new Y.Doc();
  const provider = new HocuspocusProvider({ url: COLLAB_URL, name: page, document: doc, token: () => ticket(user, level, credential) });
  const synced = new Promise((resolve) => provider.on("synced", resolve));
  return { doc, provider, synced };
}

/** Appends a paragraph the way Tiptap stores it in the `default` fragment. */
function type(doc, text) {
  const p = new Y.XmlElement("paragraph");
  p.insert(0, [new Y.XmlText(text)]);
  doc.getXmlFragment("default").push([p]);
}

const content = (doc) => doc.getXmlFragment("default").toString();

async function stored() {
  const s = await web(`/internal/collab/pages/${page}/state`);
  const doc = new Y.Doc();
  if (s.ydoc) Y.applyUpdate(doc, Buffer.from(s.ydoc, "base64"));
  return { revision: s.revision, doc, text: content(doc) };
}

const revisions = () => web(`/internal/collab/pages/${page}/revisions`);

async function waitFor(what, cond, ms = 20_000) {
  for (const end = Date.now() + ms; Date.now() < end; await sleep(200)) if (await cond()) return;
  throw new Error(`timed out waiting for ${what}`);
}

const storedHas = (...words) => async () => {
  const { text } = await stored();
  return words.every((w) => text.includes(w));
};

const op = (body, headers) => call(`${COLLAB_HTTP}/documents/${page}/ops`, { method: "POST", body: JSON.stringify(body), headers });

const scenarios = {
  // AT-10: a viewer's forged Yjs update is rejected.
  async at10() {
    const editor = client(EDITOR, "edit");
    const viewer = client(VIEWER, "view");
    await Promise.all([editor.synced, viewer.synced]);
    assert(viewer.provider.authorizedScope === "readonly", `viewer scope is ${viewer.provider.authorizedScope}`);
    assert(editor.provider.authorizedScope === "read-write", `editor scope is ${editor.provider.authorizedScope}`);

    type(viewer.doc, "forged"); // bypasses the read-only editor and sends a raw update
    await sleep(500);
    type(editor.doc, "after");
    await waitFor("the editor's edit to persist", storedHas("after"));

    assert(!content(editor.doc).includes("forged"), "the forged update reached another client");
    assert(!(await stored()).text.includes("forged"), "the forged update was persisted");
    assert(!(await revisions()).some((r) => r.contributors.includes(VIEWER)), "the viewer is listed as a contributor");
  },

  // AT-11: human and agent edits in one persist window; the revision lists both, the agent credential,
  // and the human who disconnected before the persist.
  async at11() {
    const human = client(EDITOR, "edit");
    const agent = client(AGENT_USER, "edit", AGENT_CREDENTIAL);
    await Promise.all([human.synced, agent.synced]);

    type(human.doc, "from the human");
    await waitFor("the human's edit to reach the agent", () => content(agent.doc).includes("from the human"));
    human.provider.destroy(); // leaves before the 2 s persist debounce fires
    type(agent.doc, "from the agent");
    await waitFor("both edits to persist", storedHas("from the human", "from the agent"));

    const revs = await revisions();
    const rev = revs.find((r) => r.contributors.includes(EDITOR) && r.contributors.includes(AGENT_USER));
    assert(rev, `no single revision lists both contributors: ${JSON.stringify(revs)}`);
    assert(rev.agent_credentials.includes(AGENT_CREDENTIAL), `agent credential missing: ${JSON.stringify(rev)}`);
    assert(rev.batch_ids.length >= 2, `expected a batch per principal: ${JSON.stringify(rev)}`);
  },

  // AT-12 (stale store): a 409 is merged with CRDT and retried without losing pending updates or batch metadata.
  async ["at12-merge"]() {
    const editor = client(EDITOR, "edit");
    await editor.synced;
    type(editor.doc, "one");
    await waitFor("the first persist", storedHas("one"));

    // Someone else stores a newer revision behind collab's back (e.g. a previous collab instance finishing late).
    const before = await stored();
    type(before.doc, "external");
    await web(`/internal/collab/pages/${page}`, {
      method: "PUT",
      body: JSON.stringify({
        ydoc: Buffer.from(Y.encodeStateAsUpdate(before.doc)).toString("base64"),
        json: { type: "doc" },
        markdown: "",
        base_revision: before.revision,
        live_epoch: 1,
        live_seq: 0,
        batches: [{ batch_id: randomUUID(), user: AGENT_USER, credential: null, op_ids: [] }],
        op_ids: [],
      }),
    });

    type(editor.doc, "two"); // collab persists on the old base, gets 409, merges, retries
    await waitFor("the merged persist", storedHas("one", "external", "two"));
    await waitFor("the merge to reach the live client", () => content(editor.doc).includes("external"));

    const revs = await revisions();
    const last = revs.at(-1);
    assert(revs.length === 3, `expected 3 revisions: ${JSON.stringify(revs)}`);
    assert(last.contributors.includes(EDITOR) && last.batch_ids.length > 0, `retried revision lost its batch: ${JSON.stringify(last)}`);
  },

  // AT-12 (restart): collab restarts mid-edit; nothing persisted is lost and the client re-syncs its unsaved updates.
  async ["at12-restart"]() {
    const editor = client(EDITOR, "edit");
    await editor.synced;
    type(editor.doc, "before");
    await waitFor("the first persist", storedHas("before"));

    type(editor.doc, "unsaved"); // inside the 2 s debounce when collab goes down
    console.log("RESTART");
    for await (const _ of createInterface({ input: process.stdin })) break;

    type(editor.doc, "after");
    await waitFor("the re-synced persist", storedHas("before", "unsaved", "after"), 60_000);
  },

  // M-B1: serialized server-side operations with a live token, persisted before the answer.
  async ops() {
    const editor = client(EDITOR, "edit");
    await editor.synced;

    const denied = await op({ op_id: randomUUID(), kind: "append", user: AGENT_USER, markdown: "x" }, { "x-collab-secret": "wrong" });
    assert(denied.status === 401, `no secret should be 401, got ${denied.status}`);

    const appendId = randomUUID();
    const appended = await op({ op_id: appendId, kind: "append", user: AGENT_USER, credential: AGENT_CREDENTIAL, markdown: "## From the agent\n\nHello there" });
    assert(appended.status === 200, `append: ${appended.status} ${JSON.stringify(appended.body)}`);
    assert(appended.body.op_id === appendId && /^\d+:\d+$/.test(appended.body.live_token), `append body: ${JSON.stringify(appended.body)}`);
    // Persisted before the answer, and the live client received it.
    const s = await stored();
    assert(s.revision === appended.body.persisted_revision && s.text.includes("Hello there"), `not persisted: r${s.revision} ${s.text}`);
    await waitFor("the op to reach the live client", () => content(editor.doc).includes("From the agent"));
    const rev = (await revisions()).at(-1);
    assert(rev.op_ids.includes(appendId) && rev.agent_credentials.includes(AGENT_CREDENTIAL), `revision lacks the op: ${JSON.stringify(rev)}`);

    // A browser edit moves the live token, so a replace with the old one is refused.
    type(editor.doc, "browser edit");
    await waitFor("the browser edit to arrive", async () => (await stored()).text.includes("browser edit"));
    const stale = await op({ op_id: randomUUID(), kind: "replace", user: AGENT_USER, markdown: "replaced", expected_live_token: appended.body.live_token });
    assert(stale.status === 409 && stale.body.error === "live_token_moved", `stale replace: ${stale.status} ${JSON.stringify(stale.body)}`);

    const replaced = await op({ op_id: randomUUID(), kind: "replace", user: AGENT_USER, markdown: "replaced", expected_live_token: stale.body.live_token });
    assert(replaced.status === 200, `replace: ${replaced.status} ${JSON.stringify(replaced.body)}`);
    const after = await stored();
    assert(after.text.includes("replaced") && !after.text.includes("Hello there"), `replace didn't replace: ${after.text}`);

    const unsupported = await op({ op_id: randomUUID(), kind: "explode", user: AGENT_USER });
    assert(unsupported.status === 400 && unsupported.body.error === "unsupported_op", `unknown kind: ${unsupported.status}`);
  },

  // T016: the Markdown sample (every block, mark and directive) goes in through the ops path; CollabTests then
  // compares the persisted projection with it.
  async markdown() {
    const sample = readFileSync(new URL("./markdown-sample.md", import.meta.url), "utf8").replaceAll("\r\n", "\n").trimEnd();
    const r = await op({ op_id: randomUUID(), kind: "append", user: AGENT_USER, credential: AGENT_CREDENTIAL, markdown: sample });
    assert(r.status === 200, `append: ${r.status} ${JSON.stringify(r.body)}`);
  },
};

const run = scenarios[process.argv[2]];
if (!run) {
  console.error(`unknown scenario ${process.argv[2]}`);
  process.exit(2);
}
run().then(
  () => {
    console.log("ok");
    process.exit(0);
  },
  (err) => {
    console.error(err);
    process.exit(1);
  },
);
