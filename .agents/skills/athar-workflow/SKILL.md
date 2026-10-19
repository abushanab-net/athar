---
name: athar-workflow
description: How an AI developer picks up, builds and finishes a change in the Athar repo. Use at the start of every coding session in this repo and before opening a PR.
---

# Athar workflow (for AI developers)

1. **Start from `origin/develop`.** `git fetch && git switch -c <topic> origin/develop`. Work on the issue or request you were given; if its scope is unclear, ask in the issue before coding.
2. **Read the code the change touches.** The code is the source of truth. The REST endpoints (`src/Athar.Web/Api`) and MCP tools (`src/Athar.Web/Mcp`) are a contract: keep existing names, inputs and outputs backward compatible and agree new ones with the maintainer.
3. **Build the smallest thing that works.** Reuse what's in the repo before adding code or packages. If you add a dependency, name it and its licence in the PR body. Schema changes go through the EF model and `dotnet ef migrations add`, never hand-written SQL, and a merged migration is never edited. New test data goes into the `qa` seed profile, not into tests or scripts.
4. **Non-negotiables:**
   - Every access check goes through `Athar.Domain.Access`.
   - Every mutation writes `AuditWriter` in the same transaction.
   - Company memory is never written directly by non-owners.
   - `org_id` on every new table.
   - Every user-facing string has English and Arabic resources.
   - No secrets in the repo.
5. **Tests after.** Add integration tests for the API and MCP surface you touched (`tests/Athar.IntegrationTests`, using Aspire.Hosting.Testing with real Postgres). For UI flows on the Playwright list, add or extend the smoke test.
6. **Run before the PR:** `python tools/verify.py` (guards, npm build, dotnet build, integration tests: the same steps as `ci.yml`) on your branch rebased on the current `origin/develop`, and paste its `verify: PASSED <sha>` line into the PR body; CI runs the same steps on the PR. If `develop` moves before you merge, rebase and verify again. Never merge on a FAILED or missing verify line.
7. **Open a small PR** against `develop`, one change per PR. The body says what changed, what you didn't do, any assumptions, and what a reviewer should look at first. Maintainers squash-merge.
8. **Stuck for more than 30 minutes, or the request looks wrong?** Stop and write the question in the PR or issue. Don't guess at scope.
