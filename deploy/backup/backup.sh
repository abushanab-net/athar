#!/bin/sh
# Athar backups (NFR-08, T098). Runs in the `backup` service of deploy/docker-compose.yml.
#
#   backup.sh schedule    one backup now (fails fast on bad config), then every night at BACKUP_AT (default)
#   backup.sh run         one backup now
#   backup.sh restore [STAMP|latest]   restore a backup into this stack (see deploy/README.md, "Restore")
#
# Each night uploads two age-encrypted archives to the KSA bucket:
#   athar-STAMP.tar.age       db.dump (pg_dump -Fc: one consistent snapshot) + attachments.tar, taken together
#   athar-keys-STAMP.tar.age  the keys volume: Data Protection key ring + OpenIddict signing/encryption certificates
# They're encrypted to BACKUP_AGE_RECIPIENT (a public key); the private key is kept off this server, so the server
# can't read its own backups. Objects older than BACKUP_RETENTION_DAYS go only after a successful upload.
set -eu
set -o pipefail  # a failing pg_dump or tar must fail the run, not leave a truncated archive behind age

: "${BACKUP_AGE_RECIPIENT:?set BACKUP_AGE_RECIPIENT in deploy/.env}"
: "${BACKUP_S3_BUCKET:?set BACKUP_S3_BUCKET in deploy/.env}"
REMOTE="ksa:${BACKUP_S3_BUCKET}/${BACKUP_S3_PREFIX:-athar}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
STATUS=/tmp/last-success

log() { echo "$(date -u +%FT%TZ) backup: $*"; }

run() {
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    # Database first, then the files it points at: a file uploaded in between is a harmless orphan, and every file the
    # dump refers to is still there (attachments are only removed by the 30-day trash purge).
    pg_dump --format=custom --no-owner --file="$work/db.dump"
    tar -C /data/attachments -cf "$work/attachments.tar" .
    tar -C "$work" -cf - db.dump attachments.tar | age -r "$BACKUP_AGE_RECIPIENT" > "$work/athar-$stamp.tar.age"
    tar -C /data/keys -cf - . | age -r "$BACKUP_AGE_RECIPIENT" > "$work/athar-keys-$stamp.tar.age"
    rclone copy --no-traverse "$work/athar-$stamp.tar.age" "$REMOTE"
    rclone copy --no-traverse "$work/athar-keys-$stamp.tar.age" "$REMOTE"
    rclone delete --min-age "${RETENTION_DAYS}d" "$REMOTE"
    date +%s > "$STATUS"
    log "uploaded athar-$stamp.tar.age ($(du -h "$work/athar-$stamp.tar.age" | cut -f1)) and its keys; kept ${RETENTION_DAYS} days"
    rm -rf "$work"
    trap - EXIT
}

restore() {
    : "${BACKUP_AGE_IDENTITY:?mount the age private key and set BACKUP_AGE_IDENTITY to its path}"
    want="${1:-latest}"
    if [ "$want" = latest ]; then
        want=$(rclone lsf "$REMOTE" --include 'athar-2*.tar.age' | sort | tail -n 1 | sed 's/^athar-//; s/\.tar\.age$//')
    fi
    [ -n "$want" ] || { log "no backups in $REMOTE"; exit 1; }
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    log "restoring $want from $REMOTE"
    rclone copy "$REMOTE/athar-$want.tar.age" "$work"
    rclone copy "$REMOTE/athar-keys-$want.tar.age" "$work"
    age -d -i "$BACKUP_AGE_IDENTITY" "$work/athar-$want.tar.age" | tar -C "$work" -xf -
    mkdir "$work/keys" && age -d -i "$BACKUP_AGE_IDENTITY" "$work/athar-keys-$want.tar.age" | tar -C "$work/keys" -xf -

    # Replace the database, attachments and keys. Stop web and worker first (deploy/README.md): FORCE ends their connections.
    db="${PGDATABASE:-athar}"
    psql -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS \"$db\" WITH (FORCE)" -c "CREATE DATABASE \"$db\""
    pg_restore --no-owner --exit-on-error -d "$db" "$work/db.dump"
    find /data/attachments -mindepth 1 -delete && tar -C /data/attachments -xf "$work/attachments.tar"
    find /data/keys -mindepth 1 -delete && cp -a "$work/keys/." /data/keys/

    log "restored $want: $(psql -tA -d "$db" -c 'select count(*) from pages') pages, $(psql -tA -d "$db" -c 'select count(*) from users') users," \
        "$(find /data/attachments -type f | wc -l) attachment files, $(find /data/keys -type f | wc -l) key files"
}

case "${1:-schedule}" in
    run) run ;;
    restore) shift; restore "$@" ;;
    schedule)
        # Each run is its own process: `set -e` doesn't apply to a function called as `run || ...`, and a failed step
        # must never be uploaded or counted as a success.
        sh "$0" run || log "FAILED (check deploy/.env and the bucket); retrying at ${BACKUP_AT:-02:00}"
        while true; do
            now=$(date +%s)
            next=$(date -d "$(date +%Y-%m-%d) ${BACKUP_AT:-02:00}" +%s)
            [ "$next" -gt "$now" ] || next=$((next + 86400))
            sleep $((next - now))
            sh "$0" run || log "FAILED; the healthcheck turns red after 26 h without a success"
        done ;;
    *) echo "usage: backup.sh schedule|run|restore [STAMP|latest]" >&2; exit 2 ;;
esac
