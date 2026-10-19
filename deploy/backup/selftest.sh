#!/usr/bin/env bash
# Round trip for deploy/backup/backup.sh (T098): backup to a throwaway S3 server, wipe, restore, compare; plus retention.
# The S3 server is `rclone serve s3` from the backup image itself, so the test pulls nothing it doesn't ship.
# Needs Docker. Everything it starts is named athar-bk-<pid> and removed at exit. CI runs it (.github/workflows/ci.yml).
#   deploy/backup/selftest.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
id="athar-bk-$$"
cleanup() {
    docker rm -f "$id-pg" "$id-s3" >/dev/null 2>&1 || true
    docker volume rm "$id-att" "$id-keys" "$id-age" >/dev/null 2>&1 || true
    docker network rm "$id" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker build -q -f deploy/backup.Dockerfile -t "$id-image" . >/dev/null
docker network create "$id" >/dev/null
for v in att keys age; do docker volume create "$id-$v" >/dev/null; done
docker run -d --name "$id-pg" --network "$id" -e POSTGRES_USER=athar -e POSTGRES_DB=athar -e POSTGRES_PASSWORD=pw pgvector/pgvector:pg17 >/dev/null
docker run -d --name "$id-s3" --network "$id" --entrypoint rclone "$id-image" serve s3 --auth-key selftest,selftest-secret --addr :9000 /srv >/dev/null
until docker exec "$id-pg" pg_isready -U athar -d athar >/dev/null 2>&1; do sleep 1; done

# An age key pair, as the PM would make off the server; the private key goes in a volume only restore mounts.
key=$(docker run --rm --entrypoint age-keygen "$id-image" 2>/dev/null)
recipient=$(echo "$key" | sed -n 's/^# public key: //p')
echo "$key" | docker run --rm -i -v "$id-age:/k" alpine sh -c 'cat > /k/key && chmod 600 /k/key'

backup() {
    docker run --rm --network "$id" -v "$id-att:/data/attachments" -v "$id-keys:/data/keys" -v "$id-age:/run/age:ro" \
        -e PGHOST="$id-pg" -e PGUSER=athar -e PGPASSWORD=pw -e PGDATABASE=athar \
        -e BACKUP_AGE_RECIPIENT="$recipient" -e BACKUP_AGE_IDENTITY=/run/age/key -e BACKUP_S3_BUCKET=selftest \
        -e RCLONE_CONFIG_KSA_TYPE=s3 -e RCLONE_CONFIG_KSA_PROVIDER=Rclone -e RCLONE_CONFIG_KSA_ENDPOINT="http://$id-s3:9000" \
        -e RCLONE_CONFIG_KSA_ACCESS_KEY_ID=selftest -e RCLONE_CONFIG_KSA_SECRET_ACCESS_KEY=selftest-secret \
        --entrypoint "$1" "$id-image" "${@:2}"
}
backup rclone mkdir ksa:selftest
psql() { docker exec "$id-pg" psql -U athar -d athar -v ON_ERROR_STOP=1 -tAc "$1"; }
fingerprint() {
    echo "db: $(psql "select string_agg(p.title || '/' || u.email, ',' order by p.id) from pages p join users u on u.id = p.id")"
    docker run --rm -v "$id-att:/a:ro" -v "$id-keys:/k:ro" alpine sh -c 'cd /a && find . -type f | sort | xargs sha256sum; cd /k && find . -type f | sort | xargs sha256sum'
}

# Sample data: tables named like the app's (restore counts pages and users), Arabic text, nested attachment folders.
psql "create table users(id int primary key, email text); create table pages(id int primary key, title text)"
psql "insert into users values (1, 'owner@example.com'), (2, 'member@example.com'); insert into pages values (1, 'Deploy runbook'), (2, 'إجراءات المناوبة')"
docker run --rm -v "$id-att:/a" -v "$id-keys:/k" alpine sh -c \
    'mkdir -p /a/org/1 && head -c 300000 /dev/urandom > /a/org/1/file.pdf && echo hi > /a/note.txt &&
     echo "<key/>" > /k/key-1.xml && head -c 2000 /dev/urandom > /k/openiddict-signing.pfx'
before=$(fingerprint)

backup /usr/local/bin/backup.sh run
# Retention: an object dated 2020 goes on the next run; today's stay. Stamps have one-second resolution, so wait for
# a new one (two runs in the same second write the same objects).
sleep 2
backup rclone touch --timestamp 2020-01-01T00:00:00 ksa:selftest/athar/athar-20200101T000000Z.tar.age
backup /usr/local/bin/backup.sh run
listing=$(backup rclone lsf ksa:selftest/athar)
if grep -q 20200101 <<<"$listing"; then echo "FAIL: retention kept a 2020 object"; exit 1; fi
[ "$(grep -c '^athar-keys-' <<<"$listing")" -eq 2 ] || { echo "FAIL: expected 2 key archives, got: $listing"; exit 1; }

# Lose everything, then restore the latest backup.
psql "drop table pages; drop table users"
docker run --rm -v "$id-att:/a" -v "$id-keys:/k" alpine sh -c 'rm -rf /a/* /k/*'
backup /usr/local/bin/backup.sh restore latest
after=$(fingerprint)

if [ "$before" != "$after" ]; then
    echo "FAIL: restore differs"; diff <(echo "$before") <(echo "$after") || true; exit 1
fi
echo "PASS: backup, retention and restore round trip ($(wc -l <<<"$after") files and rows compared)"
