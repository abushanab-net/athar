# Session-capture hooks

**Easiest:** run the one-line setup from **Agents › Quick setup** (`/agents`): `curl -fsSL <athar>/agent/setup.sh | bash`, or `irm <athar>/agent/setup.ps1 | iex` on Windows. It asks for your token without echoing it, installs the Claude Code plugin (`agent-plugin/`: this hook, the skill and Athar over OAuth), and sets up Codex and VS Code. The Agents page also fills in every snippet below for that server.

During development everything is local: the server is `https://localhost:7100` (the Aspire `web` resource). The public host `athar-mcp.example.com` replaces it in Phase P.

1. Copy `athar-hook.mjs` to `~/.athar/athar-hook.mjs`. It needs Node 22.15 or later, for `--use-system-ca`: the local server uses the ASP.NET dev certificate, which Node only trusts through the OS store (run `dotnet dev-certs https --trust` once). Never turn off TLS checks instead.
2. Create `~/.athar/config.json` containing `{"url":"https://localhost:7100","token":"athar_pat_…"}`. Use a PAT with the `read` and `memory:write` scopes. **Capture only runs for opted-in repos**: choose them on **My sessions** (`/sessions`), where **Pause** also stops everything. The hook reads that list at session start and caches it in `~/.athar/settings.json` for offline use (T136). On one machine, `"capture_repos": [...]` adds repos and `"capture_exclude": [...]` leaves some out. On Windows, replace `~` with the full path (for example `C:/Users/you/.athar/athar-hook.mjs`); hook commands don't expand `~`.
3. Add the MCP server and the hooks:

**Claude Code** (`~/.claude/settings.json`):
```json
{
  "hooks": {
    "SessionStart": [{ "hooks": [{ "type": "command", "command": "node --use-system-ca ~/.athar/athar-hook.mjs start claude-code", "timeout": 10 }] }],
    "PreCompact":   [{ "hooks": [{ "type": "command", "command": "node --use-system-ca ~/.athar/athar-hook.mjs commit claude-code", "timeout": 30 }] }],
    "SessionEnd":   [{ "hooks": [{ "type": "command", "command": "node --use-system-ca ~/.athar/athar-hook.mjs commit claude-code", "timeout": 30 }] }]
  }
}
```
Add the server with `claude mcp add --transport http athar https://localhost:7100/mcp --header "Authorization: Bearer athar_pat_…"`.

**Codex** (`~/.codex/hooks.json`). Codex uses the same hook format. `Stop` fires at the end of each turn. Each commit carries a `commit_id` and a record range, and the server ignores repeated commits and records it already has:
```json
{
  "hooks": {
    "SessionStart": [{ "hooks": [{ "type": "command", "command": "node --use-system-ca ~/.athar/athar-hook.mjs start codex", "timeout": 10 }] }],
    "Stop":         [{ "hooks": [{ "type": "command", "command": "node --use-system-ca ~/.athar/athar-hook.mjs commit codex", "timeout": 30 }] }]
  }
}
```
Add the MCP server in `~/.codex/config.toml` under `[mcp_servers.athar]`, with `url = "https://localhost:7100/mcp"` and a bearer token header.

**Codex hook trust.** Hooks are a stable feature in Codex (`codex features list` shows `hooks`), but Codex only runs hooks that have **persisted trust**. Until you trust these two, Codex skips them silently and nothing is captured.
- Approve them once in an interactive `codex` session after adding them. Check the exact prompt in Codex's hooks documentation for your version; it wasn't exercised here.
- Non-interactive `codex exec` won't run untrusted hooks.
- `--dangerously-bypass-hook-trust` runs hooks without trust for one invocation. It's meant for automation that vets hook sources itself, so don't use it for day-to-day capture.

**Claude Desktop and claude.ai (Phase P):** these need the public agent host `https://athar-mcp.example.com/mcp` with OAuth, which isn't set up during local development. Use Claude Code or Codex locally until then.

**Reliability:** every pending batch is written to `~/.athar/outbox` before it's sent. A lock left by a killed hook is recovered automatically once its owner process is gone. In the rare case that a hook was killed during recovery itself, capture pauses until you stop all agent sessions and run `node ~/.athar/athar-hook.mjs unlock claude-code`. Nothing is lost meanwhile, because pending batches stay in the outbox. Run `node --use-system-ca ~/.athar/athar-hook.mjs flush claude-code` to resend manually. Capture of the final turn is best effort.

4. Install the skill: copy `agent-skills/athar-brain/` to `~/.claude/skills/` (Claude Code) and `~/.agents/skills/` (Codex).

## Validated versions (T134, 2026-10-11, Windows 11)
Pinned: the plugin is `athar@athar` **1.0.0** (`agent-plugin/athar/.claude-plugin/plugin.json`), and the minimum client versions
below are the ones the setup and the hub target. The OAuth clients are `claude-code` (callback 3118), `codex` (3119) and `vscode`
(127.0.0.1:33418 or the vscode.dev relay).

| Client | Version | What was checked |
|---|---|---|
| Claude Code | **2.1.296** (min 2.1.288) | `claude plugin validate` passes for the marketplace and the plugin. Installed from the setup bundle into a scratch `CLAUDE_CONFIG_DIR`, `claude mcp get plugin:athar:athar` reports `OAuth: client_id configured, callback_port 3118`. The capture hooks run through `${CLAUDE_PLUGIN_ROOT}`. Earlier (T065, 2.1.288): a real `claude -p` run captured one session per `session_id`, every record, and redacted a `password=…` secret |
| Codex CLI | **0.160.1** (min 0.160) | The `config.toml` that `setup.mjs` writes (`[mcp_servers.athar]` plus `[mcp_servers.athar.oauth]` with `client_id = "codex"`, `callback_port = 3119`) loads: `codex mcp list` shows `athar` as streamable HTTP, "Not logged in" (OAuth). Lane C validated the flow against the production metadata in T133. The AT-30 Codex leg (a hook commit producing inbox candidates) passed at dev complete. Hooks are trust-gated (see above) |
| VS Code (Copilot agent mode) | **1.141.0** (min 1.141) | `code --add-mcp` with the server entry the hub and `setup.mjs` use (`"oauth": {"clientId": "vscode"}`) is accepted into the user `mcp.json` of a scratch profile. No capture hooks for VS Code (G-3) |
| Node | 26.3.1 (needs ≥ 22.15) | `node athar-hook.test.mjs` and `node tests/cli/athar.test.mjs` pass; the setup scripts refuse older versions |

**Live sign-in** with each real app (browser consent, then a recall) needs the `codex` and `vscode` clients on the server, which
production gets from the `minimal` seed at the T145 deploy: check it there and add the date here.

Re-validate when a client changes its hook payload (`session_id`, `transcript_path`, `cwd`) or its hook trust rules, and update this table. Self-test: `node athar-hook.test.mjs`.
