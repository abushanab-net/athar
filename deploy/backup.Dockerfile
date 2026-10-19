# Athar backups (NFR-08, T098): pg_dump for Postgres 17, age for encryption, rclone for the S3-compatible KSA bucket.
#   docker build -f deploy/backup.Dockerfile -t athar-backup .

FROM alpine:3.21
# Remotes come from RCLONE_CONFIG_KSA_* (deploy/docker-compose.yml); no config file.
ENV RCLONE_CONFIG=/dev/null
RUN apk add --no-cache postgresql17-client age rclone tar tzdata
COPY deploy/backup/backup.sh /usr/local/bin/backup.sh
RUN chmod +x /usr/local/bin/backup.sh
ENTRYPOINT ["/usr/local/bin/backup.sh"]
CMD ["schedule"]
