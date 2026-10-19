// Athar agent setup (T132, FR-AG-08). Run by setup.sh / setup.ps1 from the unpacked /agent/bundle.zip:
//   ATHAR_TOKEN=<pat> node setup.mjs <bundle-dir> <server-url> <mcp-url>
// The PAT arrives in the environment, never on the command line, and is written only to ~/.athar/config.json (0600),
// which the capture hook reads. MCP itself signs in with OAuth (claude-code, codex and vscode clients, docs/06 §6.1).
// Re-running is safe: every step merges into what's there and leaves other servers, hooks and settings alone.
import { existsSync, mkdirSync, readFileSync, writeFileSync, cpSync, rmSync, chmodSync } from "node:fs";
import { homedir, platform } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";

const [bundle, server, mcp] = process.argv.slice(2);
const token = process.env.ATHAR_TOKEN ?? "";
const home = homedir(), athar = join(home, ".athar");
const fwd = (p) => p.replaceAll("\\", "/"); // hook commands run in sh or cmd: forward slashes work in both
const done = [], todo = [];

const readJson = (p, fallback) => { try { return JSON.parse(readFileSync(p, "utf8")); } catch { return existsSync(p) ? null : fallback; } };
const writeJson = (p, v) => { mkdirSync(join(p, ".."), { recursive: true }); writeFileSync(p, JSON.stringify(v, null, 2) + "\n"); };
const has = (cmd) => { try { execFileSync(cmd, ["--version"], { stdio: "ignore", shell: platform() === "win32" }); return true; } catch { return false; } };
const run = (cmd, args) => execFileSync(cmd, args, { stdio: "inherit", shell: platform() === "win32" });

async function main() {
  if (!bundle || !server || !mcp) throw new Error("usage: ATHAR_TOKEN=<pat> node setup.mjs <bundle-dir> <server-url> <mcp-url>");
  if (!/^athar_pat_\S+$/.test(token)) throw new Error("That isn't an Athar access token (athar_pat_…). Create one on the Agents page.");
  const check = await fetch(`${server}/api/v1/inbox`, { headers: { authorization: `Bearer ${token}` } }).catch((e) => ({ status: e.message }));
  if (check.status !== 200) throw new Error(`The token didn't work on ${server} (${check.status}). Check it hasn't expired or been revoked.`);

  // 1. The hook's config: url and token, keeping the person's capture choices.
  const cfgPath = join(athar, "config.json");
  const cfg = readJson(cfgPath, {}) ?? {};
  writeJson(cfgPath, { ...cfg, url: server, token, capture_repos: cfg.capture_repos ?? [] });
  try { chmodSync(cfgPath, 0o600); } catch {}
  cpSync(join(bundle, "athar-plugin", "athar", "hooks", "athar-hook.mjs"), join(athar, "athar-hook.mjs"));
  // T143: the athar CLI (folder ingest), with shims in ~/.athar/bin for sh and cmd.
  cpSync(join(bundle, "athar.mjs"), join(athar, "athar.mjs"));
  mkdirSync(join(athar, "bin"), { recursive: true });
  writeFileSync(join(athar, "bin", "athar"), `#!/bin/sh\nexec node --use-system-ca "${fwd(join(athar, "athar.mjs"))}" "$@"\n`);
  try { chmodSync(join(athar, "bin", "athar"), 0o755); } catch {}
  writeFileSync(join(athar, "bin", "athar.cmd"), `@node --use-system-ca "${join(athar, "athar.mjs")}" %*\r\n`);
  done.push(`~/.athar/config.json (capture repos: ${(cfg.capture_repos ?? []).join(", ") || "none yet"})`);

  // 2. Claude Code: the plugin (skill, hooks, MCP over OAuth) from a local marketplace in ~/.athar/plugin.
  if (has("claude")) {
    const market = join(athar, "plugin");
    rmSync(market, { recursive: true, force: true });
    cpSync(join(bundle, "athar-plugin"), market, { recursive: true });
    try { run("claude", ["plugin", "marketplace", "add", market]); } catch { run("claude", ["plugin", "marketplace", "update", "athar"]); }
    try { run("claude", ["plugin", "install", "athar@athar"]); } catch { run("claude", ["plugin", "update", "athar@athar"]); }
    done.push("Claude Code: plugin athar@athar");
    todo.push("Claude Code: run /mcp in a session and sign in to athar");
  }

  // 3. Codex: MCP over OAuth, the skill and the capture hooks.
  const codexHome = process.env.CODEX_HOME ?? join(home, ".codex");
  if (has("codex") || existsSync(codexHome)) {
    const toml = join(codexHome, "config.toml");
    const text = existsSync(toml) ? readFileSync(toml, "utf8") : "";
    if (/^\[mcp_servers\.athar\]/m.test(text)) {
      todo.push(`Codex: ${fwd(toml)} already has [mcp_servers.athar]; left as is`);
    } else {
      mkdirSync(codexHome, { recursive: true });
      writeFileSync(toml, `${text}${text && !text.endsWith("\n") ? "\n" : ""}\n[mcp_servers.athar]\nurl = "${mcp}"\n\n[mcp_servers.athar.oauth]\nclient_id = "codex"\ncallback_port = 3119\n`);
    }
    cpSync(join(bundle, "athar-plugin", "athar", "skills", "athar-brain"), join(home, ".agents", "skills", "athar-brain"), { recursive: true });
    const hooksPath = join(codexHome, "hooks.json");
    const hooks = readJson(hooksPath, {});
    if (hooks === null) todo.push(`Codex: ${fwd(hooksPath)} isn't valid JSON; add the hooks from the Agents page`);
    else {
      hooks.hooks ??= {};
      const hook = fwd(join(athar, "athar-hook.mjs"));
      for (const [event, mode, timeout] of [["SessionStart", "start", 10], ["Stop", "commit", 30]]) {
        const list = (hooks.hooks[event] ??= []);
        if (!JSON.stringify(list).includes("athar-hook.mjs"))
          list.push({ hooks: [{ type: "command", command: `node --use-system-ca ${hook} ${mode} codex`, timeout }] });
      }
      writeJson(hooksPath, hooks);
    }
    done.push("Codex: MCP server, athar-brain skill and capture hooks");
    todo.push("Codex: run `codex mcp login athar`, then approve the two Athar hooks once in an interactive session");
  }

  // 4. VS Code (Copilot agent mode): MCP over OAuth. No capture hooks for VS Code.
  const userDir = platform() === "win32" ? join(process.env.APPDATA ?? join(home, "AppData", "Roaming"), "Code", "User")
    : platform() === "darwin" ? join(home, "Library", "Application Support", "Code", "User") : join(home, ".config", "Code", "User");
  if (existsSync(userDir)) {
    const mcpJson = join(userDir, "mcp.json");
    const conf = readJson(mcpJson, {});
    if (conf === null) todo.push(`VS Code: ${fwd(mcpJson)} has comments or isn't JSON; add the server from the Agents page`);
    else {
      conf.servers ??= {};
      conf.servers.athar = { type: "http", url: mcp, oauth: { clientId: "vscode" } };
      writeJson(mcpJson, conf);
      done.push("VS Code: athar in your user mcp.json");
      todo.push("VS Code: start athar from Copilot Chat's tools list and sign in");
    }
  }

  console.log("\nAthar is set up:\n" + done.map((d) => `  ✓ ${d}`).join("\n"));
  if (todo.length) console.log("\nNext:\n" + todo.map((t) => `  • ${t}`).join("\n"));
  console.log("\nChoose which repositories to capture on My sessions (/sessions).");
  console.log(`Add a folder of documents to a project: ${fwd(join(athar, "bin", "athar"))} ingest <folder> --project <id> (add ${fwd(join(athar, "bin"))} to your PATH to type just "athar").`);
}

main().catch((e) => { console.error(`\nAthar setup stopped: ${e.message}`); process.exit(1); });
