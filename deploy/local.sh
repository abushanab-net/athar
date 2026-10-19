#!/usr/bin/env bash
# Run the Athar production stack on this machine in Docker, built from this checkout (deploy/local.override.yml).
#
#   deploy/local.sh up      # build, start, wait until healthy, print the URLs (first run creates deploy/.env.local)
#   deploy/local.sh down    # stop; data stays in the athar-local volumes
#   deploy/local.sh reset   # stop and delete the local data (the next up seeds again)
#   deploy/local.sh logs    # follow the logs
#   deploy/local.sh ps      # container status
#
# Optional, before the first up: ATHAR_AI_API_KEY (model gateway key; without it search is degraded), ATHAR_SEED
# (qa by default; minimal for an empty org), ATHAR_LOCAL_PORT (8180 by default; uses that port and the next 2).
set -euo pipefail
cd "$(dirname "$0")/.."

env_file=deploy/.env.local
compose=(docker compose -p athar-local -f deploy/docker-compose.yml -f deploy/local.override.yml --env-file "$env_file")

secret() { head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'; }

new_env() {
  local port=${ATHAR_LOCAL_PORT:-8180}
  cat > "$env_file" <<EOF
# Local Docker stack (deploy/local.sh). Git-ignored: holds generated secrets. Delete it with 'reset' to start over.
ATHAR_IMAGE_PREFIX=athar-local
ATHAR_TAG=dev
ATHAR_SEED=${ATHAR_SEED:-qa}
POSTGRES_PASSWORD=$(secret)
COLLAB_SECRET=$(secret)
ATHAR_APP_URL=http://localhost:$port
ATHAR_MCP_URL=http://localhost:$((port + 1))
ATHAR_SHARE_URL=http://localhost:$((port + 2))
ATHAR_LAN_PORT=$port
ATHAR_MCP_PORT=$((port + 1))
ATHAR_SHARE_PORT=$((port + 2))
ATHAR_WEB_PORT=$((port + 3))
ATHAR_MAILPIT_PORT=$((port + 5))
ATHAR_AI_BASE_URL=${ATHAR_AI_BASE_URL:-http://192.168.1.10:8080/v1}
ATHAR_AI_API_KEY=${ATHAR_AI_API_KEY:-none}
ATHAR_SMTP_HOST=mailpit
ATHAR_SMTP_PORT=1025
ATHAR_SMTP_STARTTLS=false
ATHAR_SMTP_FROM=athar@localhost
# The backup service is off locally (profile never), but Compose still checks these.
BACKUP_AGE_RECIPIENT=unused
BACKUP_S3_ENDPOINT=unused
BACKUP_S3_BUCKET=unused
BACKUP_S3_ACCESS_KEY_ID=unused
BACKUP_S3_SECRET_ACCESS_KEY=unused
EOF
  echo "Created $env_file"
}

value() { grep -E "^$1=" "$env_file" | cut -d= -f2-; }

case "${1:-up}" in
  up)
    [ -f "$env_file" ] || new_env
    "${compose[@]}" up -d --build --wait
    echo
    echo "Athar is running:"
    echo "  App       $(value ATHAR_APP_URL)"
    echo "  Agents    $(value ATHAR_MCP_URL)/mcp  (setup: $(value ATHAR_MCP_URL)/agent/setup.sh)"
    echo "  Mailpit   http://localhost:$(value ATHAR_MAILPIT_PORT)"
    if [ "$(value ATHAR_SEED)" = qa ]; then
      echo "  Sign in   qa.admin@qa.athar.local / Qa-password-1!  (any qa user, e.g. qa.member@qa.athar.local)"
    else
      echo "  Owner     ${compose[*]} run --rm web bootstrap --email you@example.com"
    fi
    [ "$(value ATHAR_AI_API_KEY)" != none ] || echo "  AI        no ATHAR_AI_API_KEY: search is degraded (keyword only)"
    ;;
  down) "${compose[@]}" down ;;
  reset) "${compose[@]}" down -v; rm -f "$env_file"; echo "Local data and $env_file removed." ;;
  logs) "${compose[@]}" logs -f --tail 100 ;;
  ps) "${compose[@]}" ps ;;
  *) echo "usage: deploy/local.sh [up|down|reset|logs|ps]" >&2; exit 2 ;;
esac
