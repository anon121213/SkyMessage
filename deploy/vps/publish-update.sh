#!/usr/bin/env bash
# Publishes an Android release on the VPS: uploads the APK into ~/skymessage/updates under a name the server does not
# serve (.part), checks its SHA-256 there against the manifest, moves it into place and only then replaces the
# manifest (latest.json) with an atomic rename, so phones only ever see a complete release. Keeps the three newest
# APKs (phones in the middle of a download can finish it). Touches nothing outside ~/skymessage/updates.
#   deploy/vps/publish-update.sh <dist/SkyMessage-0.2.57.apk> <dist/latest.json>
# The manifest comes from deploy/vps/release-manifest.py. Environment as in deploy.sh: SKY_HOST, SKY_SSH_PORT,
# SKY_SSH_KEY, SKY_KNOWN_HOSTS, SKY_REMOTE_DIR.
set -euo pipefail

if [ "$#" -ne 2 ]; then
    echo "usage: $0 <SkyMessage-x.y.z.apk> <latest.json>" >&2
    exit 2
fi
APK="$1"
MANIFEST="$2"
HOST="${SKY_HOST:-JasperMaglot@77.73.131.184}"
PORT="${SKY_SSH_PORT:-22345}"
KEY="${SKY_SSH_KEY:-$HOME/.ssh/skymessage_vps}"
REMOTE="${SKY_REMOTE_DIR:-skymessage}"
SSH="ssh -o IdentitiesOnly=yes -o IdentityAgent=none -i $KEY -p $PORT"
if [ -n "${SKY_KNOWN_HOSTS:-}" ]; then
    SSH="$SSH -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$SKY_KNOWN_HOSTS"
fi

name="$(basename "$APK")"
case "$name" in
    SkyMessage-*.apk) ;;
    *) echo "unexpected APK name: $name (expected SkyMessage-<version>.apk)" >&2; exit 1 ;;
esac
# The manifest must describe exactly this file.
read -r listed sha < <(python3 -I -c 'import json, sys; a = json.load(open(sys.argv[1], encoding="utf-8"))["android"]; print(a["file"], a["sha256"])' "$MANIFEST")
if [ "$listed" != "$name" ]; then
    echo "the manifest names $listed, not $name" >&2
    exit 1
fi
if command -v sha256sum >/dev/null; then local_sha="$(sha256sum "$APK" | cut -d' ' -f1)"; else local_sha="$(shasum -a 256 "$APK" | cut -d' ' -f1)"; fi
if [ "$local_sha" != "$sha" ]; then
    echo "the manifest's SHA-256 does not match $APK" >&2
    exit 1
fi

echo "== upload $name"
$SSH "$HOST" "mkdir -p $REMOTE/updates"
rsync -az --partial -e "$SSH" "$APK" "$HOST:$REMOTE/updates/$name.part"
rsync -az -e "$SSH" "$MANIFEST" "$HOST:$REMOTE/updates/latest.json.part"

echo "== verify and publish"
$SSH "$HOST" "sh -s -- '$REMOTE/updates' '$name' '$sha'" <<'REMOTE_SCRIPT'
set -eu
cd "$1"
actual=$(sha256sum "$2.part" | cut -d' ' -f1)
if [ "$actual" != "$3" ]; then
    echo "SHA-256 mismatch after the upload; nothing published" >&2
    rm -f -- "$2.part" latest.json.part
    exit 1
fi
chmod 644 "$2.part" latest.json.part
mv -f -- "$2.part" "$2"
# The APK is complete before the manifest names it.
mv -f latest.json.part latest.json
ls -1t -- SkyMessage-*.apk | tail -n +4 | while IFS= read -r old; do
    [ "$old" = "$2" ] || rm -f -- "$old"
done
ls -l
REMOTE_SCRIPT
