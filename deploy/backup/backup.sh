#!/usr/bin/env bash
# Backup of the database and attachment files (TASK-070). NOT TESTED against a real PostgreSQL (none was available when written).
# Usage: backup.sh <backup-dir>
# Environment: PGHOST PGPORT PGUSER PGDATABASE (password via PGPASSFILE or PGPASSWORD from the secret store),
#              STORAGE_ROOT (attachments, default /var/lib/ksc/files), BACKUP_RECIPIENT (age recipient; optional but recommended).
set -euo pipefail
dest="${1:?backup directory}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
work="$dest/ksc-$stamp"
mkdir -p "$work"
umask 077

pg_dump --format=custom --no-owner --file="$work/db.dump"
tar -C "${STORAGE_ROOT:-/var/lib/ksc/files}" -czf "$work/files.tar.gz" .
# Attachment hashes recorded in the database, used by verify-restore.sh to prove files and records still match.
psql -At -c 'select "Id", "Sha256", "StorageKey" from meeting_attachments order by "Id"' > "$work/attachments.tsv"
( cd "$work" && sha256sum db.dump files.tar.gz attachments.tsv > SHA256SUMS )

if [ -n "${BACKUP_RECIPIENT:-}" ]; then
  tar -C "$dest" -cf - "ksc-$stamp" | age -r "$BACKUP_RECIPIENT" > "$dest/ksc-$stamp.tar.age"
  rm -rf "$work"
  echo "$dest/ksc-$stamp.tar.age"
else
  echo "WARNING: BACKUP_RECIPIENT not set, backup is not encrypted" >&2
  echo "$work"
fi

# Retention: keep the newest N (default 14) backups.
keep="${KEEP:-14}"
ls -1dt "$dest"/ksc-* 2>/dev/null | tail -n +"$((keep + 1))" | xargs -r rm -rf
