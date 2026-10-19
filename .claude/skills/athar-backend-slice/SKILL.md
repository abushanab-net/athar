---
name: athar-backend-slice
description: Pattern for adding a server feature in Athar end to end - a domain rule, an EF query, a REST endpoint, an MCP tool, the permission check, the audit write and an integration test. Use when implementing any API, MCP tool or worker job.
---

# Backend slice pattern

**Order:** domain → data → service → REST + MCP (both thin) → test.

1. **Domain** (`src/Athar.Domain/<Area>`): plain C# rules with no I/O. For example, `MemoryRules.CanWrite(actor, memory)` returns `Allow`, `Propose` or `Deny`.
2. **Data** (`src/Athar.Data`): add queries to the area's query class.
   - Compose the `Athar.Domain.Access` EF predicates into LINQ.
   - For raw vector or FTS SQL, pass the materialized visible set **and** `org_id = @org` explicitly.
   - Never filter after `LIMIT`.
   - If the model changes, ask Lane A. Schema changes are EF model changes plus `dotnet ef migrations add`, never hand-written SQL.
   - Add any fixtures the feature needs to the `qa` seed with fixed IDs.
3. **Service** (`src/Athar.Web/Services/<Area>Service.cs`): one method per use case, in this order:
   1. open the transaction;
   2. `access.Require(...)`, re-checked inside the transaction;
   3. mutate;
   4. `audit.Write(...)`;
   5. insert the `jobs` rows (idempotent `(kind, key)`, carrying the owner revision or hash);
   6. **commit last**.

   Writes take an `op_id`, which is durable: record the operation row in the **same transaction** as the mutation and audit (FR-RT-07). Where relevant they also take `expected_revision` (memories) or `expected_live_token` (pages). Page content changes never write the DB directly: they go through `collab /documents/{id}/ops` (architecture §4.2).
4. **REST** (`src/Athar.Web/Api/<Area>Endpoints.cs`): a minimal-API group under `/api/v1`. Map DTO ⇄ service. Return problem details on errors.
5. **MCP** (`src/Athar.Web/Mcp/<Area>Tools.cs`): `[McpServerToolType]` with `[McpServerTool]` methods calling the **same service**. Tool names, inputs and outputs are a contract: keep the existing ones backward compatible and agree new ones with the PM. Take the actor from `IActorContext`, which resolves the authenticated user, credential and OAuth client, plus the token scopes and disclosure class. `clientInfo` is stored only as an untrusted claimed label. Authorize tools, resources and prompts the same way.
6. **Worker job** (if any): `src/Athar.Worker/Jobs/<Name>Job.cs` implementing `IJob`. It claims the job inside a transaction: a raw **query** (`FromSql … FOR UPDATE SKIP LOCKED`), then a new `lease_id` and `lease_until` are set on the tracked entity and saved. No SQL `UPDATE`. Renewal and completion reload the job `FOR UPDATE` and continue only if the `lease_id` still matches (`xmin` is the concurrency token). A superseded worker discards its work. Jobs are idempotent. It discards itself if the owner revision or hash has moved on.
7. **Integration test:** start the AppHost with `DistributedApplicationTestingBuilder` using the `qa` seed, and call REST and MCP (`McpClient` over HTTP) as the seeded users. Assert the result, the permission denials (including the `external-agent` disclosure), **and** the `audit_events` row.

Watch out for:
- Optimistic concurrency: update tools take `expected_revision`. Return a conflict when it doesn't match.
- Arabic text: go through `TextNormalizer.Normalize()` at index time and at query time.
- Never log request bodies that may contain page content or transcripts.
