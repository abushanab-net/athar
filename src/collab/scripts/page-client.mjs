// One live Yjs client for the page-op tests (tests/Athar.IntegrationTests/PageOpsTests.cs).
// Environment: WEB_URL, COLLAB_URL, COLLAB_SECRET, PAGE, USER_ID, LEVEL (view|edit). Commands:
//   relpos <quote>   prints {"from","to"}: base64 Yjs relative positions around the first occurrence of <quote>
//   type <text>      appends a paragraph, prints "typed" once collab has it, and stays connected (unsaved) until stdin closes
//   watch <text>     prints "ready", then "seen" once the document no longer contains <text>, then exits
//   forge <text>     writes a paragraph even on a read-only connection, then exits (collab must drop it)
//   delete <text>    deletes <text>, exits once collab has it
//   insert <needle> <text>  prints "ready", then on a "go" line types <text> inside <needle>, exits once collab has it
import { HocuspocusProvider } from "@hocuspocus/provider";
import { createInterface } from "node:readline";
import * as Y from "yjs";

const { WEB_URL, COLLAB_URL, COLLAB_SECRET, PAGE: page, USER_ID, LEVEL = "view" } = process.env;
const [command, arg] = process.argv.slice(2);

const res = await fetch(`${WEB_URL}/internal/collab/tickets`, {
  method: "POST",
  headers: { "content-type": "application/json", "x-collab-secret": COLLAB_SECRET },
  body: JSON.stringify({ user: USER_ID, page, level: LEVEL }),
});
const { ticket } = await res.json();
const doc = new Y.Doc();
const provider = new HocuspocusProvider({ url: COLLAB_URL, name: page, document: doc, token: ticket });
await new Promise((resolve) => provider.on("synced", resolve));
const fragment = doc.getXmlFragment("default");
const text = () => fragment.toString();

function* texts(node) {
  for (const child of node.toArray()) {
    if (child instanceof Y.XmlText) yield child;
    else if (child instanceof Y.XmlElement) yield* texts(child);
  }
}

if (command === "relpos") {
  for (const t of texts(fragment)) {
    const plain = t.toDelta().map((d) => (typeof d.insert === "string" ? d.insert : "")).join("");
    const at = plain.indexOf(arg);
    if (at < 0) continue;
    const enc = (i) => Buffer.from(Y.encodeRelativePosition(Y.createRelativePositionFromTypeIndex(t, i))).toString("base64");
    console.log(JSON.stringify({ from: enc(at), to: enc(at + arg.length) }));
    process.exit(0);
  }
  console.error(`"${arg}" not found`);
  process.exit(1);
}

if (command === "type") {
  const p = new Y.XmlElement("paragraph");
  p.insert(0, [new Y.XmlText(arg)]);
  fragment.push([p]);
  while (provider.hasUnsyncedChanges) await new Promise((r) => setTimeout(r, 50));
  console.log("typed");
  for await (const _ of createInterface({ input: process.stdin }));
  process.exit(0);
}

/** The text run holding `needle`, and where. */
function find(needle) {
  for (const t of texts(fragment)) {
    const plain = t.toDelta().map((d) => (typeof d.insert === "string" ? d.insert : "")).join("");
    const at = plain.indexOf(needle);
    if (at >= 0) return { t, at };
  }
  throw new Error(`"${needle}" not found`);
}

const settle = async () => {
  while (provider.hasUnsyncedChanges) await new Promise((r) => setTimeout(r, 50));
  await new Promise((r) => setTimeout(r, 300));
};

if (command === "forge") {
  // A read-only connection writing anyway (AT-10/15): collab must drop it.
  const p = new Y.XmlElement("paragraph");
  p.insert(0, [new Y.XmlText(arg)]);
  fragment.push([p]);
  await new Promise((r) => setTimeout(r, 1000));
  process.exit(0);
}

if (command === "delete") {
  const { t, at } = find(arg);
  t.delete(at, arg.length);
  await settle();
  process.exit(0);
}

if (command === "insert") {
  // insert <needle> <text>: types <text> in the middle of <needle>, keeping the formatting there (like typing inside an
  // anchor). Prints "ready" with the spot fixed as a relative position, and types on a "go" line, so two clients can
  // edit the same range at once.
  const { t, at } = find(arg);
  const spot = Y.createRelativePositionFromTypeIndex(t, at + Math.floor(arg.length / 2));
  console.log("ready");
  for await (const _ of createInterface({ input: process.stdin })) break;
  const abs = Y.createAbsolutePositionFromRelativePosition(spot, doc);
  abs.type.insert(abs.index, process.argv[4]);
  await settle();
  process.exit(0);
}

if (command === "watch") {
  console.log("ready");
  while (text().includes(arg)) await new Promise((r) => setTimeout(r, 100));
  console.log("seen");
  process.exit(0);
}

console.error(`unknown command ${command}`);
process.exit(2);
