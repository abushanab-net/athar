// ponytail: one runnable check for the athar CLI's own logic (T143). Run: node tests/cli/athar.test.mjs
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { gitignore, listFiles, plan, parseArgs, MAX_FILE_BYTES } from "../../agent-plugin/setup/athar.mjs";

// .gitignore: dir/, anchored, globs, ** and negation
const ig = gitignore("", "# comment\n\nbuild/\n/secret.md\n*.log\ndocs/**/draft-*.md\n!docs/keep/draft-ok.md\n");
assert.equal(ig("build", true), true);
assert.equal(ig("src/build", true), true, "dir/ matches at any depth");
assert.equal(ig("build.md", false), false);
assert.equal(ig("secret.md", false), true);
assert.equal(ig("sub/secret.md", false), false, "anchored");
assert.equal(ig("notes/app.log", false), true);
assert.equal(ig("docs/a/b/draft-1.md", false), true);
assert.equal(ig("docs/keep/draft-ok.md", false), false, "negated");

// listing without git: text documents only, hidden and ignored folders skipped
const root = mkdtempSync(join(tmpdir(), "athar-cli-"));
const put = (p, text) => { mkdirSync(join(root, p, ".."), { recursive: true }); writeFileSync(join(root, p), text); };
put(".gitignore", "build/\n*.tmp.md\n");
put("README.md", "# Readme\n\nhello");
put("docs/guide.txt", "guide");
put("docs/page.html", "<p>page</p>");
put("docs/photo.png", "png");
put("build/out.md", "generated");
put("notes.tmp.md", "scratch");
put(".hidden/x.md", "hidden");
put("node_modules/pkg/README.md", "dependency");
const files = listFiles(root, { git: () => null });
assert.deepEqual(files, ["README.md", "docs/guide.txt", "docs/page.html"]);

// plan: everything is new the first time; afterwards only changed files are sent; oversized files are skipped
const first = plan(root, files, {});
assert.equal(first.batches.flat().length, 3);
put("docs/guide.txt", "guide, edited");
const second = plan(root, files, first.hashes);
assert.deepEqual(second.batches.flat().map((f) => f.path), ["docs/guide.txt"]);
put("big.md", "x".repeat(MAX_FILE_BYTES + 1));
assert.deepEqual(plan(root, [...files, "big.md"], second.hashes).skipped, ["big.md"]);

assert.deepEqual(parseArgs(["ingest", "./docs", "--project", "p1", "--confidential"]), { _: ["ingest", "./docs"], project: "p1", confidential: true });
rmSync(root, { recursive: true, force: true });
console.log("athar CLI self-test ok");
