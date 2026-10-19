# Athar user guide

Athar is your team's wiki and shared memory. People write pages. AI agents read the pages and the memories, and save new memories while you work. This guide covers what you can do as a member. Admins also have the [admin guide](/help#admin).

## Getting started

- **Sign in** with the account from your invitation. Pick your language (English or Arabic) and theme from the top bar.
- **Your profile** (`/account/profile`) holds your display name and photo. Your photo shows in comments, presence and page bylines.
- **Home** shows recent pages, your favorites and what needs your attention.
- To connect an AI agent, go to [Agents](#agents).

## Wiki

Pages live in **spaces**, and spaces live in **workspaces**. You see a workspace's spaces only when you're a member of the workspace. Each space gives you a role: viewer, commenter, editor or admin.

- **Create a page** with **New page** or the **+** on a tree row. New pages start as **drafts**. Only people who can edit see a draft. **Publish** makes it visible to everyone in the space, and to search and agents.
- **Edit and Read.** Editors switch between Edit and Read at the top of the page; your choice is remembered in this browser. Read shows the page without the toolbar.
- **Editing together.** Everyone edits the same page live and sees each other's cursors. Changes save on their own. Earlier versions are under **⋯ › Page history**, where you can restore one.
- **Formatting.** Type `/` for the block menu: headings, lists, tables, code, images and Mermaid diagrams (a code block with the `mermaid` language).
- **The side panel** shows the page's **Outline** first, then **Comments** and **Versions**. **Share** opens from the page bar.
- **Comments.** Select text and add a comment, or comment on the whole page, from the box at the bottom of the Comments tab. The **@** button mentions someone; they get a notification. Reply in the thread's Reply box, and use its **⋮** menu to edit, resolve or delete. Ctrl+Enter sends.
- **The page menu (⋯)** has Copy link, Copy as Markdown, Watch, Full width, Move, Duplicate, Copy to space, Print, Export and Move to trash. Trashed pages can be restored for 30 days.
- **Watching.** You watch the pages you create or comment on. Watchers hear about edits (at most once every 30 minutes), comments and trashing.

## Sharing

- **Share** on a page gives people or teams view, comment or edit access to that page only.
- **External links** are read-only links for people outside Athar, for a page (optionally with its subpages) or a whole space. They can have an expiry date and a password. Restricted subpages are never included. Space admins can list and revoke links.

## Import and export

A space's admins can bring pages in from other wikis, workspace admins can bring in whole Confluence sites, and anyone who can read a page can take a copy out.

- **Import** from the space's **Pages ⋯ › Import pages…** (pages land at the top of the space) or a tree row's **⋮ › Import here…** (pages land under that page). Choose one file:
  - a **Markdown** (`.md`) or **HTML** file: one page;
  - a **zip** of them: a page's subpages go in a folder named like the page (`Guide.md` and `Guide/Setup.md`), which is also the layout Athar and Docmost export;
  - a **Confluence export**, HTML or XML (Space tools › Export): the page tree, info and warning panels, code blocks, images and files come across, files under their real names. Files a page lists but doesn't link to are attached under an **Attachments** heading. From an XML export, comments come across too, as page comments with their replies: they appear under your name, and each starts with who wrote it in Confluence and when.
- **A whole Confluence site** goes in from **Workspaces › Import spaces…** (workspace admins). Each Confluence space becomes a space in that workspace, named like it, with its key as the URL name and you as its admin. If a space with that URL name already exists and you manage it, the pages go there.
- **What you get.** Imported pages are **published** straight away, so search and agents find them within a few minutes. Links between imported pages point to the new pages. Images, PDFs and Word files become attachments; other file types are skipped and listed in the report.
- **Big imports.** Up to 4 GB and 20,000 pages per import, files of any size. The upload shows its progress; then the import runs in the background with a page count, and you can close the dialog: a notification tells you when it's done or why it failed. Very large PDFs and Word files (over 100 MB) are kept but not searched inside. Only the current version of each Confluence page comes across: no history, drafts or blog posts.
- **Export** from the page menu: **Export as Markdown**, **Export as HTML**, or **Export with subpages (.zip)**; or the whole space from **Pages ⋯ › Export space (.zip)**. A page with files comes as a zip of the page and its files. A zip holds the pages and their files and imports straight back into Athar; links to pages that aren't in it open them in Athar. You only get the pages you can read.

## Search

Search (`/search`, or the box in the top bar) finds pages, memories and project sources you can read. It understands English and Arabic, including different spellings of the same Arabic letter. Filter by kind, space or project.

When your admins have assigned a query model, **Answer from these results** writes a short answer from the top results you can read, with numbered links to its sources. It only runs when you ask, uses no earlier conversation, and leaves out spaces whose AI policy doesn't allow AI to read them. Check the sources before relying on it. With a vision model assigned, the text in new PNG, JPEG and WebP attachments (up to 25 MB) is searchable too.

## Dashboard

**Dashboard** (`/dashboard`, after Wiki in the sidebar) shows what your agents did with the company's knowledge. You see your own activity: calls made with your tokens and your agents' sign-ins. Owners and admins can switch between **Mine** and **All** to see the whole organization.

- **Cards:** knowledge you can read (pages, memories, skills, sources); agent calls today, split into reads and writes; retrievals today and how many found nothing; active agents in the last 24 hours and tokens about to expire or unused for 30 days.
- **Second row:** memories promoted to company knowledge in the last 30 days, proposals waiting for review and how long the oldest has waited, how often a recall found something, and (for admins) the index jobs.
- **Agent calls per day:** the last 14 days, stacked by client. Fridays and Saturdays are shaded.
- **Writes:** a year of memories, session commits, pages and sources, one square per day, with the busiest day and the last write.
- **From sessions to company knowledge:** captured sessions, the memories and proposals they produced, and how many were reviewed and promoted.
- **Most recalled memories** and **Needs attention:** memories nobody has recalled in 30 days, open conflicts and searches that keep finding nothing. A memory you can't read appears without its title.
- **MCP tools:** your own calls per tool, the 95th-percentile response time and the error rate.
- **For admins:** model tokens per day by role, and the job queue with its last error. **Access tokens** shows your own tokens.

Today's numbers are live. Earlier days come from a nightly summary, so yesterday's numbers fill in after it runs.

## Memories

A **memory** is a short, durable fact: a decision, a convention, a procedure, a policy, a preference. Each memory has a **scope** that decides who sees it and who may change it:

| Scope | Who sees it | Who changes it |
|---|---|---|
| Personal | Only you | You |
| Team | The team | Team members |
| Project | Project members | Project members |
| Company | Everyone | Knowledge owners. Anyone else's change becomes a **proposal** |

- Browse and filter memories at `/memories`. Each memory keeps its full revision history and its links (to pages, sessions or URLs).
- **Promote** a section of a page into a memory from the page.
- **Project overrides.** A project admin can shadow a company memory with a project-specific version, unless the company memory is marked non-overridable (policies are by default). If the company memory changes later, the shadow is flagged as stale.
- **Confidential** memories and spaces are never shown to external agents.

## Review

Review (`/review`) is where decisions wait for you:

- **Inbox:** memory candidates that agents extracted from your sessions. Edit one, pick its scope and accept it, or discard it. Similar existing memories are listed beside it; nothing is merged automatically.
- **Proposals:** changes to company memories (and company skills) waiting for a knowledge owner. Proposers see the status of theirs.
- **Conflicts:** two memories that disagree. Anyone who can read both can flag a conflict. Resolving it needs permission to change each memory involved.

## Agents

**Agents** (`/agents`) is the one place to connect an AI agent to Athar. The status card shows when each agent last sent a session and when your token was last used.

**Quick setup** connects every agent on your computer in one step. Open **Agents › Quick setup** and run the command for your system: `curl -fsSL <athar>/agent/setup.sh | bash` (macOS, Linux), or `irm <athar>/agent/setup.ps1 | iex` (Windows). Paste your access token when asked; it isn't shown or saved in your shell history. Setup configures whichever of these it finds: the Claude Code plugin, Codex and VS Code. Then sign in once in each: `/mcp` in Claude Code, `codex mcp login athar`, and the athar server in VS Code. Running setup again is safe.

To set a client up by hand instead:

1. Create a token once (for the capture hooks, and for clients that can't sign in through a browser).
2. Pick your client's tab and copy each snippet.
3. For Claude Code and Codex, list the repositories you want captured first (see [Capture](#capture)).

- **Claude apps** (claude.ai, Desktop, mobile) sign in with your Athar account and need no token.
- **Codex** and **VS Code** sign in through your browser too (OAuth). If a machine can't open a browser, use a token instead; the tab shows how.
- Agents work through the same permissions as you. They also follow each space's AI policy, which decides whether agents may read, write or trash pages there.

The "Getting started with agents" page in the wiki walks through the setup step by step.

## Capture

When you opt a repository in, your coding agent's session is **captured**: the hook sends the transcript, redacted, to Athar. Athar then **extracts** memory candidates from it.

- **What is captured** is set on [My sessions](/sessions). It lists the repositories captured on the server. On each machine, `capture_repos` in `~/.athar/config.json` adds repositories and `capture_exclude` removes them. Nothing is captured for a repository that isn't on either list.
- **Pause** stops all capture at once, whatever each machine says. Saving chats is refused while paused.
- **Redaction** removes common token formats and `key=value` secrets before anything leaves your machine, and again on the server. It can't catch every secret or personal detail, so keep secrets out of your prompts.
- **Privacy.** Only you can read your transcripts, admins included. They're deleted 30 days after they arrive, or right away with **Delete now**.
- **Auto-accept.** Personal candidates become memories at once, unless you turn that off. Project and team candidates have their own switch, off by default, and only apply where you may publish. Company candidates always go to a knowledge owner. Each auto-accepted memory can be undone from My sessions, which archives it.
- **Save a chat.** In a Claude app or VS Code, ask the agent to "Save this chat to Athar" (the `save-chat` prompt). It summarizes the chat, asks you to confirm the project, and saves it as a session. Extraction then runs on it like on a coding session.

## My sessions

**My sessions** (`/sessions`) shows what your agents and chats sent, grouped by project:

- each session's source (coding agent or chat), client, size and the date its transcript will be deleted;
- what extraction made of it: memories (with **Undo** for auto-accepted ones), proposals, and candidates still waiting in your inbox;
- the transcript (only for you), and **Delete now**, which removes the transcript and its unreviewed candidates right away. Memories you already saved stay.

## Skills

**Skills** (`/skills`) lists the agent skills you can use: yours, your teams' and projects', and the company's.

- Search or filter by scope, then open a skill to read it.
- **Install:** download the zip, run the install command for Claude Code or Codex, and restart the agent.
- **Share:** choose **Publish a skill** and upload its `SKILL.md` (or a zip of its folder), then pick personal, a team or a project. Publishing the same name again adds a revision; **Version history** shows each one.
- Company skills go through a knowledge owner, like company memories.

## Projects

A **project** ties a team's repositories to its memories, sources and agent sessions (`/projects`). Members see their projects; project admins manage members, repositories and sources.

A project's **sources** are its outside knowledge, and only the project's members find them in search:

- **GitHub repositories**, synced by Athar.
- **Web pages:** choose **Add a web page** and paste one public address. Athar keeps a snapshot; **Refresh** takes a new one.
- **Documents:** choose **Upload a document** (Markdown, text, HTML, PDF, Word, PowerPoint or Excel, up to 10 MB; only the text is kept).
- **A folder of documents:** run `athar ingest <folder> --project <project id>` (Quick setup installs `athar` in `~/.athar/bin`). It sends the Markdown, text and HTML files, skipping what `.gitignore` ignores. The folder shows on the project page. Run the command again to update it: only changed files are sent, and files you deleted leave search.
- Agents add web pages with `add_source`.
- A source that couldn't be read shows an error: check the address or the file, then Refresh.

## Notifications

The bell shows mentions, replies, shares with you, watched-page updates, proposal decisions and conflicts on memories you own. **See all** opens `/notifications`.

## Tokens

Personal access tokens (`/account/tokens`) let scripts, hooks and agents act as you, limited to the scopes you choose (`read`, `write`, `memory:write`, `pages:delete`). A token is shown once; revoke it there when you no longer need it. Revoking takes effect on the next request.
