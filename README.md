# Athar · أثر

A collaborative wiki with a memory. People write specs, runbooks and decisions together, in English and Arabic. The AI agents that work with them (Claude Code, Codex, the Claude apps, VS Code) share one governed memory over MCP: they recall what the team decided before they act, and they remember what gets settled, in Athar rather than in a context file on one laptop.

[![Athar. Knowledge that stays (intro video, 3 min)](https://img.youtube.com/vi/warq2lPl67Y/maxresdefault.jpg)](https://youtu.be/warq2lPl67Y)

**Intro video (3 min):** [youtu.be/warq2lPl67Y](https://youtu.be/warq2lPl67Y).

## What it does

- **Wiki:** workspaces, spaces and a page tree; real-time co-editing with live cursors; page and inline comments with threads; versions with restore; share links with password and expiry; drafts, trash and favourites.
- **Migration:** import whole Confluence spaces (page tree, attachments, comments), Markdown, HTML and zips; export any page or space as Markdown, HTML or zip.
- **Search:** every save is chunked and embedded in the background. Hybrid search (keyword plus vectors) across pages, memories, sources and skills, in Arabic and English, with cited answers.
- **Second brain:** memories with a type (decision, convention, procedure, policy, incident, glossary and more), a scope (personal, project, team, company) and a revision history. Company memory changes only through a proposal that a knowledge owner approves, rejects or sends back for a rebase.
- **Agents:** MCP and REST with the same permissions as the user; `search`, `recall`, `remember`, `promote_to_memory`, `save_chat`; published skills that agents must follow; opt-in capture of coding sessions into a review inbox, with secrets redacted and transcripts deleted after 30 days.
- **Governance:** audit log with CSV and JSON export, token and OAuth client management, per-space AI policy, and a dashboard of what agents do with company knowledge.
- **Your servers, your models:** self-hosted; point it at any OpenAI-compatible endpoint on your network and change models without a restart.

The user, admin and agent guides live in [`docs/handbook`](docs/handbook) (English and Arabic) and under **Help** inside the app.

## Stack

.NET 10 · Blazor Server with MudBlazor · .NET Aspire · PostgreSQL 17 with pgvector · Tiptap and Yjs with a Hocuspocus sidecar · MCP C# SDK · OpenIddict for agent OAuth · Ollama or any OpenAI-compatible gateway for embeddings and extraction.

| Folder | What is in it |
|---|---|
| `src/Athar.Web` | The Blazor app, the REST API (`Api/`) and the MCP server (`Mcp/`) |
| `src/Athar.Worker` | Background jobs: embedding, extraction, GitHub sync, uploads, retention |
| `src/Athar.Data` | EF Core model, migrations and the seed profiles (`minimal`, `qa`, `perf`) |
| `src/Athar.Domain` | Domain rules (memory, promotion, scopes) |
| `src/Athar.AppHost` | The Aspire host for local development |
| `src/editor`, `src/collab` | The Tiptap editor island and the Hocuspocus collaboration server |
| `agent-skills`, `agent-plugin` | The `athar-brain` skill, the capture hooks and the Claude Code plugin bundle |
| `deploy` | Docker Compose, Caddy, backups and the production runbook |
| `tests` | Integration tests (Aspire.Hosting.Testing), perf and CLI tests |
| `docs/handbook` | The product handbook: user, admin and agent guides, English and Arabic |

## Set it up

You need:

- Docker Desktop (or Docker Engine with the Compose plugin). Postgres, Ollama and Mailpit run as containers.
- The .NET SDK pinned in [`global.json`](global.json) (10.0.400 or a later 10.0 feature band).
- Node.js 22.
- Git.

Then:

```sh
git clone https://github.com/abushanab-net/athar.git
cd athar
dotnet dev-certs https --trust     # the app serves https://localhost:7100
dotnet build                       # also builds the editor island (npm ci + npm run build in src/editor)
```

Search and extraction use the Ollama container by default; it pulls `bge-m3` on first use, so the first search is slow. To use an OpenAI-compatible gateway instead, set the base URL and model ids in `src/Athar.AppHost/appsettings.json` and the key as a user secret:

```sh
dotnet user-secrets set Parameters:ai-key <key> --project src/Athar.AppHost
```

An admin can also change the endpoint and models later under **Admin › AI models**.

## Run it

### For development (Aspire)

```sh
dotnet run --project src/Athar.AppHost
```

The console prints the Aspire dashboard link. The app is at https://localhost:7100, the MCP endpoint at https://localhost:7100/mcp, and every email lands in the `mailpit` resource. The database is migrated and seeded with the `qa` profile at start. Sign in as `qa.owner@qa.athar.local` with `Qa-password-1!`; the other accounts and tokens are in [`Demo-Credentials.md`](Demo-Credentials.md).

- **Reset the database:** the **Reset database** command on the `web` resource in the dashboard, or `dotnet run --project src/Athar.Web -- db reset` with the connection string set. It drops, migrates and seeds again. Production refuses it.
- **Other seed:** `dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf`.

### The production stack, locally (Docker)

```sh
deploy/local.ps1 up      # PowerShell
deploy/local.sh up       # bash
```

The first run writes `deploy/.env.local` with generated secrets, builds the images and waits until everything is healthy. The app is at http://localhost:8180, the agents host (MCP, OAuth, setup) at http://localhost:8181, and Mailpit at http://localhost:8185. `down` keeps the data, `reset` deletes it, `logs` and `ps` do what they say. Set `ATHAR_AI_API_KEY` before the first `up` for real search; set `ATHAR_SEED=minimal` for an empty organization.

### Connect an agent

In the app, open **Agents › Quick setup** and run the one command it shows; it configures Claude Code, Codex and VS Code. By hand, for Claude Code against the Docker stack:

```sh
claude mcp add --transport http athar http://localhost:8181/mcp --header "Authorization: Bearer athar_pat_qa_member"
```

### Tests and local CI

```sh
dotnet test tests/Athar.IntegrationTests   # needs Docker; starts the stack with Aspire.Hosting.Testing
python tools/verify.py                     # the whole CI pipeline locally; --no-tests for guards and builds only
```

### Production

[`deploy/README.md`](deploy/README.md) covers the server, first start and the first owner, upgrades, the three public hosts behind the reverse proxy, nightly encrypted backups, restore and the go-live checklist.

## Contribute

1. **Branch from `develop`** and open one pull request per change against `develop`. Keep it small: one task, one PR.
2. **Verify before you ask for review.** Rebase on the current `origin/develop`, run `python tools/verify.py` (the same steps CI runs on every pull request), and paste its `verify: PASSED <sha>` line into the PR body. A PR without a passing line and green CI is not merged.
3. **Tests go in with the feature.** Integration tests under `tests/Athar.IntegrationTests` for server work, Playwright smoke tests for the main UI flows. No TDD is required.
4. **Database changes go through the EF model.** Change the model, then `dotnet ef migrations add <Name> -p src/Athar.Data -s src/Athar.Web`. Never hand-write SQL, never edit or squash a merged migration, and put test data in the `qa` seed with fixed IDs. The production database is data-preserving: anything that would lose data needs the owner's decision first.
5. **Both languages, always.** Every user-facing string has an English and an Arabic resource, and the screen works right to left.
6. **The API and MCP tools are a contract.** Keep existing endpoint and tool shapes backward compatible, and agree new ones with the product owner before building.
7. **AI developers** start with [`CLAUDE.md`](CLAUDE.md) and the `athar-*` skills in `.claude/skills` (mirrored in `.agents/skills` for Codex).

Bug reports and ideas: open an issue with what you did, what you expected and what happened, plus the seed profile and the commit you ran.

No open-source license has been chosen yet; ask before reusing the code outside this organization.
