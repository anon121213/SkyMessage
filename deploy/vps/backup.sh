#!/usr/bin/env sh
# Daily PostgreSQL dump of everything (messages and media), run from the user's crontab on the VPS. Keeps the last 3:
# media makes dumps large. A restore needs the same STORAGE_MASTER_KEY (.env).
set -eu
umask 077
cd "$(dirname "$0")"
mkdir -p backups
file="backups/skymessage-$(date +%F).dump"
docker compose exec -T db pg_dump -U skymessage -Fc skymessage > "$file.tmp"
mv "$file.tmp" "$file"
ls -1t backups/skymessage-*.dump | tail -n +4 | xargs -r rm --
echo "$(date -Is) backup ok: $file ($(du -h "$file" | cut -f1))"
