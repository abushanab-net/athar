# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users
- **Engineers (pilot users)**, about 10 people in one company at first. They write specs and runbooks in the wiki, connect Claude Code and Codex to Athar, and review what their agents captured. They switch between the wiki and their editor many times a day.
- **Knowledge owners (IT, DevOps, HR)** approve and maintain company-wide memory through the proposals queue.
- **Organization admins** manage users, teams, workspaces, tokens and the audit log.
- **External readers** open shared pages through read-only links (password and expiry optional).
- **AI agents** (Claude Code, Codex) are first-class users through MCP and REST, not the web UI.

## Product Purpose
Athar (أثر, "trace, effect") is one place where a company writes knowledge together and where every connected AI agent remembers what was learned, respects who may change what, and answers from the wiki, GitHub and memories together, with sources. Success: the team writes in Athar, agents share one memory, answers are grounded and cited, and governance holds (no unauthorized writes to company memory, everything audited).

## Positioning
A collaborative wiki and a governed shared memory for AI agents in one product. Knowledge is filed by scope (personal, project, team, company). Company memory is read-only except for knowledge owners, agents and members propose, owners approve, and a project can shadow a company memory locally without changing it. Agent sessions are captured into a review inbox instead of being lost.

## Operating Context
- Daily work: page tree, rich editor with real-time co-editing, inline and page comments with @mentions, versions with restore, sharing.
- Second brain: memory browser (scope, type, status, project filters), capture inbox (accept, edit, discard), proposals queue, conflicts.
- Search across pages, memories, sources and skills (hybrid, Arabic and English).
- Admin: users, teams, workspaces, space settings, tokens (PATs), audit log with CSV/JSON export.
- Signed-out surfaces: sign-in, forgot/reset password, accept invite, OAuth consent for agents, public share pages.
- Desktop browsers first; phone width must work (navigation folds into a menu).

## Capabilities and Constraints
- Blazor (InteractiveServer) with MudBlazor 8; account forms are static SSR (plain inputs, no interactivity).
- Every user-facing string is localized (English and Arabic resources).
- No cloud services: inference is local (Ollama). The pilot runs on one company host.
- **Open decisions:** the public page before sign-in is new (the app has none today); its content must not claim customers, metrics or capabilities Athar doesn't have.

## Brand Commitments
- Name: **Athar** / **أثر**, used in both scripts.
- No logo exists yet. A mark is to be proposed with the visual direction and also serve as the favicon.
- The current brain-teal theme is **not** binding; the owner wants it replaced.

## Evidence on Hand
- Real product docs: the handbook in `docs/handbook` (user guide, admin guide, agents guide, English and Arabic).
- QA seed content (pages, memories, inbox items, Arabic and English) for realistic screens.
- No customers, testimonials, usage numbers or press exist. Never fabricate them.

## Product Principles
1. **Operate first.** People come to write, find and approve. Every screen serves the task before the look.
2. **Trust is visible.** Scope, ownership, approval state and sources are always shown, never hidden behind a click.
3. **Two languages, one product.** Arabic and English are equal: RTL and LTR layouts, mixed-direction content, and both scripts get first-class typography.
4. **Reuse over one-offs.** Shared controls (tables, row actions, page headers, comment items, form fields) are built once and used everywhere.
5. **Calm density.** Compact, consistent spacing and modest type sizes; information-dense screens stay readable.

## Accessibility & Inclusion
- Keyboard operation and a visible focus ring on the main flows (NFR-06); WCAG 2.2 AA is the R1 target.
- RTL layout and mixed-direction text (NFR-05).
- Icon-only actions need accessible names and tooltips.
