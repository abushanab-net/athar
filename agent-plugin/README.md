# Athar agent plugin and setup (T132)

Athar serves everything here itself, so nobody needs access to this repository:

- `/agent/bundle.zip`: the Claude Code marketplace in `.claude-plugin/` and the `athar` plugin (`.mcp.json` with Athar
  over OAuth as the `claude-code` client, `hooks/hooks.json` for opt-in session capture), plus `setup/setup.mjs`. The
  web build embeds this folder and adds the athar-brain skill and the capture hook from `agent-skills/`
  (`src/Athar.Web/Services/Agent/AgentSetup.cs`). `{{MCP_URL}}`, `{{SERVER_URL}}` and `{{BASE_URL}}` are filled per request.
- `/agent/setup.sh` and `/agent/setup.ps1`: ask for the PAT without echoing it, download the bundle and run `setup.mjs`.
  The PAT goes only into `~/.athar/config.json`, which the capture hook reads. MCP signs in with OAuth.

`setup/athar.mjs` is the `athar` CLI (T143): setup copies it to `~/.athar/athar.mjs` with `~/.athar/bin/athar` and `athar.cmd`
shims. `athar ingest <folder> --project <id>` sends the folder's text documents to `POST /api/v1/sources/files`, honoring
`.gitignore`, and re-runs send only what changed (`~/.athar/ingest/`). Self-test: `node tests/cli/athar.test.mjs`.

`setup.mjs` merges, so re-running is safe. It installs the plugin when `claude` is on the PATH, configures Codex when
`codex` or `~/.codex` exists, and VS Code when its user folder exists. Check the plugin with `claude plugin validate`.
