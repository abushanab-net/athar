#!/usr/bin/env node
// Athar CLI (T143, FR-KS-06). Installed by /agent/setup.sh and setup.ps1 into ~/.athar.
//   node ~/.athar/athar.mjs ingest <folder> [--project <id>] [--label <name>] [--confidential]
// Sends the folder's text documents (Markdown, text, HTML, reStructuredText, AsciiDoc) to a project as one source. Files
// ignored by git (.gitignore) are skipped, and so are hidden folders. Re-runs send only what changed since the last run
// (a hash manifest in ~/.athar/ingest/); files that are gone are removed on the server. Uses ~/.athar/config.json.
import { readFileSync, existsSync, writeFileSync, mkdirSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join, relative, resolve, basename, extname, sep } from "node:path";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";

export const TEXT_TYPES = [".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".html", ".htm"];
export const MAX_FILE_BYTES = 1024 * 1024;   // per file, like GitHub docs
export const MAX_BATCH_BYTES = 4 * 1024 * 1024;
export const MAX_FILES = 2000;

/** The folder's candidate files, relative with '/', honoring .gitignore (via git when the folder is in a work tree). */
export function listFiles(root, { git = gitFiles } = {}) {
  const viaGit = git(root);
  const all = viaGit ?? walk(root, root);
  return all.filter((p) => TEXT_TYPES.includes(extname(p).toLowerCase()) && !p.split("/").some((s) => s.startsWith(".")))
    .sort();
}

function gitFiles(root) {
  try {
    if (execFileSync("git", ["rev-parse", "--is-inside-work-tree"], { cwd: root, stdio: ["ignore", "pipe", "ignore"] }).toString().trim() !== "true") return null;
    const out = execFileSync("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "."], { cwd: root, stdio: ["ignore", "pipe", "ignore"], maxBuffer: 64 * 1024 * 1024 });
    return out.toString("utf8").split("\0").filter(Boolean).filter((p) => existsSync(join(root, p)));
  } catch { return null; }
}

/** Without git: a walk that skips hidden folders and node_modules, and applies the root .gitignore's simple patterns. */
function walk(root, dir, ignore = gitignore(root)) {
  const out = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, e.name), rel = relative(root, full).split(sep).join("/");
    if (e.name.startsWith(".") || e.name === "node_modules" || ignore(rel, e.isDirectory())) continue;
    if (e.isDirectory()) out.push(...walk(root, full, ignore)); else if (e.isFile()) out.push(rel);
  }
  return out;
}

/** A small .gitignore matcher: blank lines and comments skipped, `dir/`, `/anchored`, `*`, `**`, `?` and `!negation`. */
export function gitignore(root, text = existsSync(join(root, ".gitignore")) ? readFileSync(join(root, ".gitignore"), "utf8") : "") {
  const rules = text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith("#")).map((l) => {
    const negate = l.startsWith("!"); if (negate) l = l.slice(1);
    const dirOnly = l.endsWith("/"); if (dirOnly) l = l.slice(0, -1);
    const anchored = l.startsWith("/") || l.includes("/"); l = l.replace(/^\//, "");
    const re = l.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\0").replace(/\*/g, "[^/]*").replace(/\?/g, "[^/]").replace(/\0/g, ".*");
    return { negate, dirOnly, rx: new RegExp(anchored ? `^${re}(/.*)?$` : `(^|/)${re}(/.*)?$`) };
  });
  return (path, isDir) => rules.reduce((ignored, r) => (r.dirOnly && !isDir && !path.includes("/")) || !r.rx.test(path) ? ignored : !r.negate, false);
}

/** Batches of changed files under the size cap; files over the per-file cap are skipped and reported. */
export function plan(root, files, previous) {
  const hashes = {}, changed = [], skipped = [];
  for (const path of files) {
    const bytes = readFileSync(join(root, path));
    if (bytes.length > MAX_FILE_BYTES) { skipped.push(path); continue; }
    const hash = createHash("sha256").update(bytes).digest("hex");
    hashes[path] = hash;
    if (previous[path] !== hash) changed.push({ path, content: bytes.toString("utf8") });
  }
  const batches = [];
  let batch = [], size = 0;
  for (const f of changed) {
    const n = Buffer.byteLength(f.content, "utf8") + f.path.length;
    if (batch.length && size + n > MAX_BATCH_BYTES) { batches.push(batch); batch = []; size = 0; }
    batch.push(f); size += n;
  }
  if (batch.length) batches.push(batch);
  return { hashes, batches, skipped };
}

async function ingest(args) {
  const folder = resolve(args._[1] ?? ".");
  if (!existsSync(folder) || !statSync(folder).isDirectory()) throw new Error(`${folder} isn't a folder.`);
  const home = join(homedir(), ".athar");
  const cfg = JSON.parse(readFileSync(join(home, "config.json"), "utf8"));
  const url = process.env.ATHAR_URL || cfg.url, token = process.env.ATHAR_TOKEN || cfg.token;
  if (!url || !token) throw new Error("Run the Athar setup first (~/.athar/config.json has no url or token).");
  const api = async (method, path, body) => {
    const r = await fetch(`${url}/api/v1${path}`, { method, signal: AbortSignal.timeout(120_000),
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" }, body: body && JSON.stringify(body) });
    const json = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(`${r.status} ${json.message ?? json.error ?? ""}`.trim());
    return json;
  };

  let project = args.project;
  if (!project) {
    let remote = null;
    try { remote = execFileSync("git", ["remote", "get-url", "origin"], { cwd: folder, stdio: ["ignore", "pipe", "ignore"] }).toString().trim(); } catch {}
    const found = remote ? await api("GET", `/projects/resolve?repo_remote=${encodeURIComponent(remote)}`) : [];
    if (found.length !== 1) throw new Error("Say which project with --project <id> (the folder's repository doesn't name exactly one).");
    project = found[0].project_id;
  }
  const label = args.label ?? basename(folder);
  const files = listFiles(folder);
  if (files.length > MAX_FILES) throw new Error(`${files.length} documents is more than ${MAX_FILES}; ingest a smaller folder.`);
  const manifestDir = join(home, "ingest"), manifest = join(manifestDir, `${project}-${label.replace(/[^A-Za-z0-9_-]/g, "_")}.json`);
  const previous = existsSync(manifest) ? JSON.parse(readFileSync(manifest, "utf8")) : {};
  const { hashes, batches, skipped } = plan(folder, files, previous);

  let source = null;
  for (let i = 0; i < Math.max(batches.length, 1); i++) {
    const last = i === Math.max(batches.length, 1) - 1;
    source = await api("POST", "/sources/files", {
      project_id: project, label, confidential: args.confidential === true, files: batches[i] ?? [],
      present: last ? files.filter((f) => hashes[f]) : null, // the last request names every file, so the server removes the rest
    });
  }
  mkdirSync(manifestDir, { recursive: true });
  writeFileSync(manifest, JSON.stringify(hashes, null, 2));
  const sent = batches.flat().length;
  console.log(`Athar: ${sent} of ${files.length - skipped.length} documents sent to "${label}" (${sent ? "changed or new" : "nothing changed"}).`);
  if (skipped.length) console.log(`Skipped (over 1 MB): ${skipped.join(", ")}`);
  console.log(`Source ${source.source_id}: ${source.documents} documents indexed for the project's members.`);
}

export function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--confidential") args.confidential = true;
    else if (a === "--project" || a === "--label") args[a.slice(2)] = argv[++i];
    else args._.push(a);
  }
  return args;
}

if (process.argv[1]?.endsWith("athar.mjs")) {
  const args = parseArgs(process.argv.slice(2));
  const run = args._[0] === "ingest" ? ingest(args) : Promise.reject(new Error("usage: athar ingest <folder> [--project <id>] [--label <name>] [--confidential]"));
  run.catch((e) => { console.error(`athar: ${e.message}`); process.exit(1); });
}
