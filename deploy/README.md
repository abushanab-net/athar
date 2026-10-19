# Deploying Athar (Phase P)

The pilot runs on one on-prem Linux server on your network, with Docker Compose. Models come from the model gateway, so
there is no Ollama container. The reverse proxy (Caddy, `deploy/Caddyfile`) is the only entry point. Staff open the
app on the server's LAN address over plain HTTP. The two public hosts come in through Pangolin, which terminates TLS.

## What runs
| Service | Image | Notes |
|---|---|---|
| `postgres` | `pgvector/pgvector:pg17` | volume `pgdata` (backed up nightly with attachments, T098) |
| `web` | `ghcr.io/abushanab-net/athar-web` | migrates the database at start, seeds `minimal`; volumes `keys`, `attachments`; health on `:8080/health` (internal only) |
| `worker` | `ghcr.io/abushanab-net/athar-worker` | jobs: embed, extract, GitHub sync, uploads, retention; starts after web is healthy |
| `collab` | `ghcr.io/abushanab-net/athar-collab` | Hocuspocus; talks to web's internal API with `COLLAB_SECRET` |
| `backup` | `ghcr.io/abushanab-net/athar-backup` | nightly encrypted backup to the KSA bucket (see Backups); healthy only after a success in the last 26 h |
| `proxy` | `caddy:2-alpine` | `deploy/Caddyfile`; the only published ports (LAN app, plus the two Pangolin targets) |

Images are built by `.github/workflows/images.yml` on every push to `develop` and `main` and on release tags (`v*`).
`deploy/docker-compose.yml` can also build them on the server with `--build`.

## Run it locally in Docker
The same stack on your own machine, built from your checkout: `deploy/local.ps1 up` (PowerShell) or `deploy/local.sh up` (bash).
The first run writes `deploy/.env.local`, which is git-ignored and holds generated secrets. It then builds the images, waits until
everything is healthy and prints the URLs. The app is at http://localhost:8180, the agents host at http://localhost:8181, and
Mailpit, where every email lands, at http://localhost:8185. Sign in as `qa.admin@qa.athar.local` with `Qa-password-1!`.

| | |
|---|---|
| `up` | build and start. Run it again after a pull: it rebuilds and keeps your data |
| `down` | stop and keep the data (Compose project `athar-local`, its own volumes) |
| `reset` | stop, delete the local data and `.env.local`; the next `up` seeds again |
| `logs`, `ps` | follow the logs, container status |

`deploy/local.override.yml` holds the differences from production:
- the `qa` seed, or `ATHAR_SEED=minimal` for an empty organization;
- Mailpit instead of the SMTP relay;
- no backup service;
- ports on localhost only;
- OAuth over plain HTTP, so agents can sign in to `http://localhost:8181`;
- the `Local` environment instead of `Production`. The app behaves the same, but `db reset` works, and web refuses
  `Athar:OAuth:AllowHttp` in `Production`.

Accounts, passwords and tokens: [`Demo-Credentials.md`](../Demo-Credentials.md).

Set `ATHAR_AI_API_KEY`, the model gateway key, before the first `up` to get real search and extraction. Without it, search falls back to keyword only. `ATHAR_LOCAL_PORT` moves the port range.

## Server preparation
1. Ubuntu 24.04 LTS (or any Linux with Docker Engine and the Compose plugin), 16 vCPU / 32 GB RAM, NVMe disk. The
   server must reach the model gateway (`http://192.168.1.10:8080`) and the SMTP relay.
2. Install Docker: `curl -fsSL https://get.docker.com | sh`, then add the deploy user to the `docker` group.
3. Sign in to GHCR once with a read-only token: `echo <token> | docker login ghcr.io -u <user> --password-stdin`.
4. Clone the repository (or copy the `deploy/` folder) to `/opt/athar`.

## First start
```bash
cd /opt/athar
cp deploy/.env.example deploy/.env       # fill in every value; generate secrets with: openssl rand -base64 32
chmod 600 deploy/.env
docker compose -f deploy/docker-compose.yml --env-file deploy/.env pull
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d
docker compose -f deploy/docker-compose.yml --env-file deploy/.env ps           # web and collab turn healthy
# The first owner (FR-ID-01): prints a one-use link to set the password.
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm web bootstrap --email owner@company.com
```

## Upgrade
```bash
# Set ATHAR_TAG in deploy/.env to the release (e.g. v0.1.0-pilot), then:
docker compose -f deploy/docker-compose.yml --env-file deploy/.env pull
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d       # web migrates before serving
```
The database is data-preserving (OD-5): upgrades only migrate forward, and `db reset` refuses to run in Production.
Roll back by retagging the previous images. If a migration went wrong, restore the pre-upgrade backup ([Restore](#restore)).

## Configuration (deploy/.env)
Every value is documented in `deploy/.env.example`. Secrets (`POSTGRES_PASSWORD`, `COLLAB_SECRET`, `ATHAR_AI_API_KEY`,
`ATHAR_SMTP_PASSWORD`) live only in `deploy/.env` on the server, which is git-ignored.

| Setting | Meaning |
|---|---|
| `ATHAR_APP_URL` | The internal host people open on the LAN (wiki UI); links in emails use it |
| `ATHAR_MCP_URL` | The public agents host (`https://athar-mcp.example.com`): OAuth issuer and MCP resource |
| `ATHAR_AI_*` | The model gateway and model ids (T090) |
| `ATHAR_SMTP_*` | The company SMTP relay (STARTTLS on 587) |

## Network and public access (T095)
Caddy publishes three ports on the server, one per host:

| Port (`deploy/.env`) | Host | What it serves |
|---|---|---|
| `ATHAR_LAN_PORT` (80) | `ATHAR_APP_URL` = `https://athar.example.com` (and the server's LAN address) | The whole app, plus the editor WebSocket at `/collab` (only the exact path; collab's HTTP API stays internal) |
| `ATHAR_MCP_PORT` (8081) | `https://athar-mcp.example.com` | `/mcp`, `/api/v1/*`, `/oauth/*`, the `/.well-known/oauth-*` and `openid-configuration` metadata, and the static files the OAuth sign-in page loads. Everything else returns 404 |
| `ATHAR_SHARE_PORT` (8082) | `https://athar-share.example.com` | `/s/*` (share pages and their attachments) and the share page's font, icon and logo. Everything else returns 404 |

The portal is `https://athar.example.com` through Pangolin (PM decision 2026-10-05); links in emails and the editor's
WebSocket use it. Staff on the office network can also open the server's address over plain HTTP, where browsers show
"Not secure" and copy buttons fall back to selecting the text.

### Pangolin (or another tunnel that terminates TLS)
In the Pangolin dashboard, on your site, create three **HTTP resources**:

| Resource domain | Target | Pangolin authentication |
|---|---|---|
| `athar-mcp.example.com` | `http://<server LAN IP>:8081` | **Off**: MCP clients and OAuth can't sign in to Pangolin. Athar does its own bearer/OAuth checks |
| `athar.example.com` | `http://<server LAN IP>:80` | Your choice: **on** adds Pangolin's sign-in in front of Athar's for the portal (agents and share links use the other hosts, so they're unaffected) |
| `athar-share.example.com` | `http://<server LAN IP>:8082` | **Off**: share links are public. A link token and an optional password protect them |

1. Create the three DNS records Pangolin asks for (usually CNAME or A records to the Pangolin server). Pangolin issues
   the certificates itself.
2. Keep Pangolin's defaults that forward the original `Host` and set `X-Forwarded-Proto: https`. Caddy trusts these
   headers only from private addresses (on 80, 8081 and 8082).
3. Never port-forward 80, 8081 or 8082 from the internet. Pangolin is the only public path.

### Check after setup
```bash
curl -s -o /dev/null -w '%{http_code}\n' https://athar-mcp.example.com/.well-known/oauth-authorization-server  # 200
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://athar-mcp.example.com/mcp                             # 401
curl -s -o /dev/null -w '%{http_code}\n' https://athar-mcp.example.com/                                        # 404
curl -s -o /dev/null -w '%{http_code}\n' https://athar-share.example.com/api/v1/search                         # 404
```
In the issuer metadata, `issuer` must be `https://athar-mcp.example.com/`.

## Backups (NFR-08, T098)
The `backup` service makes one backup when it starts, then one every night at `BACKUP_AT` (02:00 Riyadh time). It
uploads two objects to `s3://$BACKUP_S3_BUCKET/$BACKUP_S3_PREFIX/` and deletes objects older than 14 days, but only
after a successful upload:

| Object | Contents |
|---|---|
| `athar-<stamp>.tar.age` | `db.dump` (`pg_dump -Fc`: one consistent snapshot) and `attachments.tar`, taken right after it |
| `athar-keys-<stamp>.tar.age` | the `keys` volume: the Data Protection key ring and the OpenIddict signing and encryption certificates |

Both are encrypted with [age](https://age-encryption.org) to `BACKUP_AGE_RECIPIENT`, a public key. **The private key
never goes on the server:** generate it on the PM's machine, store it offline (a password manager and a printed copy),
and put only the public key in `deploy/.env`:
```sh
age-keygen -o athar-backup.key     # prints "Public key: age1..." -> BACKUP_AGE_RECIPIENT
```
Without that key the backups can't be read, and without the keys archive restored, sign-in cookies, encrypted GitHub
tokens and OAuth tokens stop working. RPO is 24 hours. RTO is under 4 hours (a restore takes minutes plus the image pull).

Check it:
```sh
docker compose -f deploy/docker-compose.yml --env-file deploy/.env ps backup          # healthy after the first backup
docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs --tail 5 backup # "uploaded athar-... and its keys"
docker compose -f deploy/docker-compose.yml --env-file deploy/.env run --rm backup run  # a backup now
```
The bucket needs a key that may list, read, write and delete objects under the prefix. A versioned bucket with an
object lock is safer still (ransomware on the server can't remove old backups); then let the bucket's lifecycle rule
expire versions after 14 days.

## Restore
On the server being restored (the same one, or a scratch host for the drill), with `deploy/.env` filled in (the same
bucket settings; the database password may be new):
```sh
C="docker compose -f deploy/docker-compose.yml --env-file deploy/.env"
$C up -d postgres
$C stop web worker collab 2>/dev/null || true
# The private key, only for this command; `latest` or a stamp from the bucket listing.
$C run --rm -v "$PWD/athar-backup.key:/run/age.key:ro" -e BACKUP_AGE_IDENTITY=/run/age.key backup restore latest
$C up -d
```
`restore` replaces the database, the attachments and the keys, then prints what it restored (pages, users, files).
Web migrates nothing it doesn't need to and keeps the restored data. Delete the key file from the host afterwards.

### Restore drill (AT-42, before go-live)
On a scratch host, restore the latest backup as above, then check, signed in as the restored users:
1. Sign in with an existing account (the password hashes and the Data Protection keys came back).
2. A restricted page is hidden from a user without access, and visible to one with it (permissions).
3. A page share and an external share link open, including a password-protected one (shares).
4. A page opens in the editor with its latest content and edits still sync (Yjs state).
5. A GitHub source syncs (its encrypted PAT decrypts with the restored keys), and an agent's PAT still works on `/mcp`.
6. An OAuth client refreshes its token (the OpenIddict certificates came back).
7. The restore took under 4 hours (RTO). Record the date, the backup stamp and the results in your operations log.

`deploy/backup/selftest.sh` runs the backup, retention and restore round trip against a throwaway S3 server (`rclone serve s3`); CI runs it
on every change to `deploy/`.

## Go-live and team onboarding (T101)
Run these steps in order. Steps 1–4 are for the PM or owner, step 5 is for each teammate, and steps 6–7 come at the end.

1. **Stack up.**
   - Fill in every value in `deploy/.env`, including the backup bucket and `BACKUP_AGE_RECIPIENT`.
   - Run `up -d` (see [First start](#first-start)).
   - `ps` should show postgres, web, collab, worker, proxy and backup healthy.
   - The backup log should say `uploaded athar-…`.
2. **First owner** (FR-ID-01). Run the `bootstrap` command from [First start](#first-start) with the owner's real email.
   - It prints a one-use link on `ATHAR_APP_URL`. Open it from the office network and set a password of 12+ characters.
   - **Never run `db reset` on the server.** The team's data is real (OD-5), and production refuses it.
3. **Organization** (signed in as the owner, on the LAN app):
   - `/admin/workspaces`: create one workspace per department or product, its spaces, and the workspace admins.
   - `/admin/teams`: create the teams that memories and projects are scoped to.
   - `/admin/users`: invite each teammate by email and pick their role. The SMTP relay sends the invitation.
     - The invitation link points at the LAN app, so teammates accept it from the office network (or a VPN).
     - Resend or revoke an invitation from the same page.
4. **Outside checks**, once Pangolin and DNS are live.
   - Make a password-protected share link (wiki → Share), and a second link that you revoke.
   - Run from a machine **off** the office network (any network outside it, e.g. a phone hotspot):
     ```sh
     python tools/check_public.py --share https://athar-share.example.com/s/<token> --password <p> \
       --gone https://athar-share.example.com/s/<revoked> --mcp https://athar-mcp.example.com
     ```
   - Then run from a machine **on** the office network:
     ```sh
     python tools/check_public.py --app https://athar.example.com   # and again with http://<server LAN IP>
     ```
   - `--rate-limit` also checks the 429 for wrong passwords. It locks that link from your address for 15 minutes, so
     use a throwaway link.
5. **Each teammate:**
   1. Accept the invitation and set a password. Sign in at `ATHAR_APP_URL`.
   2. Open **Connect your agent** (account menu → `/connect`). It does all of the following:
      - creates a personal access token (`read` + `memory:write`; `write` only if they ask);
      - shows the MCP address on the public agent host;
      - offers the `athar-brain` skill and the capture hook as downloads;
      - fills in the Claude Code and Codex snippets.
      The full manual steps are in `agent-skills/hooks/README.md`.
   3. Session capture only runs for the repositories listed in `~/.athar/config.json` (opt-in), and secrets are
      redacted before upload. What the worker extracts from a session arrives as **proposals** for a knowledge owner
      to review.
   4. Codex users approve the two hooks once in an interactive `codex` session. Codex skips untrusted hooks silently.
   5. Claude Desktop, claude.ai and Claude mobile connect through OAuth: see [Claude as a connector](#claude-as-a-connector).
6. **Smoke test with one teammate.**
   - Their agent answers a question from the wiki through `/mcp`.
   - A captured session's extracted candidates show up in `/proposals`.
   - The knowledge owner approves one. It then appears in `/memories`, and the agent can recall it.
   - `/admin/tokens` shows the token's last use.
7. **Pilot starts.**
   - Tell the team where to give feedback.
   - Watch `/proposals` (the review queue), the `backup` health, and `docker compose ... logs --since 24h web worker`
     for errors.
   - OD-5 is declared and `v0.1.0-pilot` is tagged (T102).

## Claude as a connector
claude.ai, Claude Desktop and Claude mobile reach Athar from Anthropic's servers, so they need the public agent host. One
connector serves all three. Athar pre-registers their OAuth client: no registration, no secret.

1. **Check from outside** (any network): `python tools/check_connector.py https://athar-mcp.example.com/mcp`. Every line
   must pass. It checks the 401, the metadata, the issuer, that the `claude-ai` client exists, and that cookies are ignored.
2. **Add and connect.** People follow **Agents › Claude apps** in Athar (`/agents`, T131): it gives the MCP server URL,
   the client ID `claude-ai` and the connector steps for each Claude plan, in English and Arabic. The URL must equal the
   token audience, and the consent page names `claude.ai` as the destination.
3. **Check it worked.** The person's **Connected apps** on `/agents` lists Claude after **Allow**.
4. **AT-31a** (the manual part, from a fresh browser profile): the flow above, then a recall in Claude. Then, in that same
   browser, open `https://athar-mcp.example.com/mcp` and `/api/v1/memories` (both 401), and a wiki page such as
   `https://athar-mcp.example.com/wiki` (404). The OAuth sign-in grants nothing outside `/oauth`.

**AT-31a passed on 2026-10-05.** The checker passed 14/14 from outside. The PM then connected claude.ai with `claude-ai`,
had Claude `remember` a personal fact and `recall` it in a new chat. On the agent host, `POST /mcp` and `/api/v1/*` give
401 without a bearer token, `GET /mcp` gives 405, wiki routes give 404, and the metadata is public.

Claude's OAuth sessions are external agents (FR-ID-06): they never see confidential content. People disconnect a connector
in Claude, or revoke the grant under **Access tokens** in Athar.
