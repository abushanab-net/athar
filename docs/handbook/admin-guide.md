# Athar admin guide

This guide is for organization owners and admins, and for workspace and space admins. Everything in the [user guide](/help#user) applies to you too. Every admin change below is recorded in the audit log.

## Roles

| Role | Where | What it allows |
|---|---|---|
| Owner | Organization | Everything an admin can do, plus trusted-local tokens and the first-owner setup |
| Admin | Organization | Users, teams, workspaces, projects and the audit log |
| Member | Organization | Uses what they're given access to |
| Workspace admin | Workspace | Creates spaces in the workspace and manages their members |
| Space admin | Space | Settings, members, classification, AI policy and share links of one space |
| Knowledge owner | Organization (a flag on a member) | Writes company memories and skills directly, and approves proposals |
| Project admin | Project | Members, repositories and sources of one project, and project overrides of company memories |

## Users

**Admin › Users** (`/admin/users`):

- **Invite** people by email. They set their own password from the invitation link.
- Change someone's organization role, or mark them a **knowledge owner**.
- **Deactivate** someone who leaves. Their sessions, tokens and OAuth grants stop working on the next request (agents included), and their captured transcripts are deleted at once. Their pages and memories stay.

## Teams

**Admin › Teams** (`/admin/teams`): create teams and add members. Teams can be given space roles and own team memories and skills.

## Workspaces and spaces

- **Workspaces** (`/workspaces`): org admins create workspaces and add members as admin or member. Only workspace members see its spaces.
- **Spaces:** workspace admins create spaces. Open **Space settings** from the wiki (the space's ⋯ menu or the quick link above the tree). The tabs are:
  - **Settings:** the name, the badge shown in menus and favorites, and the **AI policy**. The policy covers what connected agents may do in the space: read, write and move pages to the trash. Each setting is independent; the default is read only.
  - **Members:** add people and teams as viewer, commenter, editor or admin; change or remove them.
  - **Security:** mark the space **confidential**. Confidential content, and everything derived from it, is never shown to external agents. Declassifying needs a stated reason.

- **Deleting a space:** workspace admins choose **Delete space…** on its row in **Workspaces** and type its name. The space disappears for everyone and all its pages go to the trash, where they are deleted for good after 30 days. Its URL name is freed, so you can create or import a space with the same one, for example to redo an import.
- **Importing a whole Confluence site:** **Import spaces…** on a workspace (see the user guide's Import and export).

## Projects

**Projects** (`/projects`): org owners and admins create projects; project admins manage them. Link the project's repositories (agents resolve the project from the repository they run in) and add GitHub sources, which sync documents that only project members can search. A GitHub source needs a token with read access; a revoked token pauses the source until you give it a new one.

## Tokens and agents

- **All tokens** (`/admin/tokens`) lists every personal access token in the organization. Admins can revoke any of them.
- Tokens and OAuth clients are **external agents** by default: they never receive confidential content. Only an owner can mark a token **trusted local**, with a reason, and only for a client that runs entirely on a company-controlled host and model.
- **OAuth clients** are pre-registered; nobody can register a new one from outside. They are Claude (claude.ai, Desktop, mobile), Claude Code, Codex and VS Code. People connect them from [Agents](/agents) and can disconnect them under their tokens page.

## Capture and privacy

- Captured transcripts are readable **only by their owner**. Admins can't read them, and there is no admin export.
- Transcripts are deleted 30 days after they arrive, and at once when their owner is deactivated.
- Memories extracted from them follow the normal scope rules. Company memories always go through a knowledge owner, never automatically.

## The "Getting started with agents" page

A new database includes a **Help** workspace with a Help space holding the "Getting started with agents" page in English and Arabic. Add people to that workspace from **Workspaces** so they can see it.

On the production server (where no seed data is added), an org admin adds the page right here, under this section: pick a space where you're an editor or admin and choose **Add the getting-started page**. Athar creates the page in English and Arabic at the root of that space. Adding it to the same space again returns the same pages, so a second click is harmless.

## Audit log

**Admin › Audit** (`/admin/audit`) lists every change: who did it, with which credential (the app, a token or an OAuth client), and what changed. Filter by person, action, resource or date, and export as CSV or JSON. Entries can't be edited or deleted.

## AI models

**Admin › AI models** (`/admin/ai-models`, owners and admins) picks the models Athar uses. Each organization starts on the **deployment defaults** (the server's own settings, or the local Ollama when the server has none). Choose **Custom endpoint** to set your own:

- **Endpoint URL**: an OpenAI-compatible endpoint on the company network (for example `http://host:8080/v1`). Loopback and private addresses only: public internet addresses, link-local and cloud metadata addresses, and redirects are refused.
- **API key**: write-only. The page shows whether a key is stored, never the key. **Keep**, **Replace** or **No key**. If you change the endpoint, enter its key again (or choose No key): a key is never sent to a different endpoint. Keys are encrypted with the server's key ring and never appear in the audit log.
- **Embedding model**: indexes pages, memories, sources and attachment text for search. Vectors are stored with 1024 values; the model must return at least that many.
- **Query model**: writes **Answer from these results** on the search page, and turns agent sessions into memory candidates.
- **Vision model** (optional): reads the text in new PNG, JPEG and WebP attachments, up to the 25 MB upload limit, so search finds it. An image with no text is done with nothing added. Images uploaded before you set it aren't read; scanned PDFs and GIFs aren't read.

**Load models** lists what the endpoint offers. **Test** sends a small made-up request to each assigned model (an embedding, a structured JSON answer, and an image when vision is set) and reports which work. Saving doesn't need the endpoint to be up.

Saved changes apply within seconds to search and background jobs, without a restart; work already running finishes with the settings it started with. While the endpoint is unreachable, indexing, extraction and image reading wait and resume when it's back; they don't fail for good. Changes to the deployment defaults themselves apply when the server restarts. Saves are checked against the version you loaded: if someone else saved first, the page shows the latest settings so you can try again.

**Changing the embedding endpoint or model re-indexes all content** in the background. Until it finishes, content not yet re-indexed is found by keywords only. The **Search index** section refreshes automatically every 3 seconds, showing the percentage, indexed chunks and queued, running or failed jobs for the current index. A rebuild may wait up to 30 seconds for the worker to queue its jobs; the page distinguishes waiting, indexing, completion and failure without replacing unsaved settings. Historical failures from earlier rebuilds are excluded. Old vectors are kept, so going back to a previous model is quick. **Rebuild index** indexes everything again under the same model name, for when the model behind that name was replaced; it works on the deployment defaults too.

**Reset to deployment defaults** stops using the saved endpoint, models and key. If the index was never rebuilt, search goes straight back to the vectors the deployment model already made; after a **Rebuild**, the deployment model indexes everything again. If the server can't decrypt a stored key (for example after its key ring was lost), the page says so: replace the key, clear it, or reset. Search keeps working on keywords meanwhile.

On first save, **Use the deployment's key** copies the server's key into the saved settings. A later change of the server's key doesn't reach saved settings: replace the key here too.

## Backups and the server

The server runbook (deployment, backups, restore drill, onboarding the first owner) is in `deploy/README.md` in the repository. Backups run nightly and include the database, attachments and keys.
