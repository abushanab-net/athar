# Demo credentials

**These work only on local and test stacks seeded with the `qa` profile:** `deploy/local.ps1 up` / `deploy/local.sh up`, the
Aspire AppHost (`dotnet run --project src/Athar.AppHost`) and the integration tests. They're public on purpose. Production
uses the `minimal` seed, so none of these accounts exist there; its first owner comes from `bootstrap` (deploy/README.md
"First start").

## Where
| | Local Docker (`deploy/local.*`) | Aspire AppHost |
|---|---|---|
| App | http://localhost:8180 | https://localhost:7100 |
| Agents host (MCP, OAuth, setup) | http://localhost:8181/mcp | https://localhost:7100/mcp |
| Mailpit (every email lands here) | http://localhost:8185 | the `mailpit` resource in the Aspire dashboard |

## Sign-in accounts
Every account below uses the same password: **`Qa-password-1!`**

| Email | Role | Use it for |
|---|---|---|
| `qa.owner@qa.athar.local` | Owner, knowledge owner | everything, including organization settings and company memory approval |
| `qa.admin@qa.athar.local` | Admin | the admin pages, the Help admin guide, publishing skills |
| `qa.member@qa.athar.local` | Member (Arabic UI) | the everyday member view, in Arabic and right to left |
| `qa.knowledge.owner@qa.athar.local` | Member, knowledge owner | reviewing proposals and company memory |
| `qa.guest@qa.athar.local` | Guest | the restricted guest view |
| `qa.deactivated@qa.athar.local` | Deactivated | checking that a deactivated account can't sign in |

The qa seed also has accounts that particular tests rely on:
- `qa.reset.user`, `qa.lockout.user` and `qa.capture.user`;
- the `lab.*` permission-matrix users;
- a second organization (`other.owner`) and a last-owner organization (`owner.a`, `owner.b`).

All of them are `@qa.athar.local`. Don't sign in to the test ones casually: some tests change their state.

## Personal access tokens (agents, hooks, REST, MCP)
Send them as `Authorization: Bearer <token>`.

| Token | User | Scopes |
|---|---|---|
| `athar_pat_qa_admin` | Qa Admin | read, write, memory:write |
| `athar_pat_qa_member` | Qa Member | read, write, memory:write |
| `athar_pat_qa_member_read` | Qa Member | read |
| `athar_pat_qa_knowledge_owner` | Qa Knowledge Owner | read, memory:write |
| `athar_pat_qa_capture_user` | Qa Capture User | read, memory:write |
| `athar_pat_qa_deactivated` | Qa Deactivated | read, memory:write (refused: the user is deactivated) |

For example, connect Claude Code to the local stack:
`claude mcp add --transport http athar http://localhost:8181/mcp --header "Authorization: Bearer athar_pat_qa_member"`.
You can also sign in with OAuth through the setup on the Agents page.

## Generated secrets
`deploy/local.*` writes the database password and the collab secret to `deploy/.env.local`. That file is git-ignored and
different on every machine. `reset` deletes it, and the next `up` generates new values.
