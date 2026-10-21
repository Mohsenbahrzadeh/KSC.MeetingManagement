#!/usr/bin/env bash
# Restores a backup into a SCRATCH database and checks it (TASK-070).
# Usage: verify-restore.sh <backup-directory (decrypted)> <scratch-database-name>
# Refuses to run against a database that already has tables, so it can never overwrite real data.
set -euo pipefail
src="${1:?decrypted backup directory}"; db="${2:?scratch database}"
[ "$db" != "${PGDATABASE:-}" ] || { echo "scratch database must differ from PGDATABASE" >&2; exit 2; }

( cd "$src" && sha256sum -c SHA256SUMS )

createdb "$db"
if [ "$(psql -At -d "$db" -c "select count(*) from information_schema.tables where table_schema='public'")" != "0" ]; then
  echo "scratch database is not empty" >&2; exit 2
fi
pg_restore --no-owner --exit-on-error -d "$db" "$src/db.dump"

fail=0
q() { psql -At -d "$db" -c "$1"; }
ok() { if [ "$2" = "$3" ]; then echo "OK   $1 ($2)"; else echo "FAIL $1: expected $3 got $2"; fail=1; fi; }

# 1. migrations applied
ok "migrations present" "$(q 'select count(*)>0 from "__EFMigrationsHistory"')" "t"
# 2. row counts equal the source (written at backup time by the caller into counts.tsv, if present)
if [ -f "$src/counts.tsv" ]; then
  while IFS=$'\t' read -r table expected; do ok "rows in $table" "$(q "select count(*) from \"$table\"")" "$expected"; done < "$src/counts.tsv"
fi
# 3. no orphans
ok "participants without meeting" "$(q 'select count(*) from meeting_participants p left join meetings m on m."Id"=p."MeetingId" where m."Id" is null')" "0"
ok "attachments without meeting" "$(q 'select count(*) from meeting_attachments a left join meetings m on m."Id"=a."MeetingId" where m."Id" is null')" "0"
ok "minutes versions without minutes" "$(q 'select count(*) from minutes_versions v left join minutes m on m."Id"=v."MinutesId" where m."Id" is null')" "0"
# 4. tenant numbering still unique
ok "duplicate meeting numbers" "$(q 'select count(*) from (select "TenantId", "Number" from meetings where "Number" is not null group by 1,2 having count(*)>1) x')" "0"
# 5. attachment files match recorded hashes
tmp="$(mktemp -d)"; tar -xzf "$src/files.tar.gz" -C "$tmp"
missing=0; bad=0
while IFS='|' read -r id sha key; do
  f="$tmp/$key"
  if [ ! -f "$f" ]; then missing=$((missing+1)); continue; fi
  actual="$(sha256sum "$f" | cut -d' ' -f1 | tr a-f A-F)"
  [ "$actual" = "$(echo "$sha" | tr a-f A-F)" ] || bad=$((bad+1))
done < <(tr -d '\r' < "$src/attachments.tsv")
rm -rf "$tmp"
ok "attachment files missing" "$missing" "0"
ok "attachment files with wrong hash" "$bad" "0"

dropdb "$db" 2>/dev/null || true
exit $fail
