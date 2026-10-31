#!/usr/bin/env bash
# Publishes an Android release on the VPS (~/skymessage/updates). The APK is uploaded under a name the server does
# not serve (.part), its SHA-256 is checked there against the manifest and only then is it moved into place, so
# phones only ever see a complete release. Keeps the three newest APKs (phones in the middle of a download can
# finish it) and the previous manifest as latest.json.prev. Touches nothing outside ~/skymessage/updates.
#
#   deploy/vps/publish-update.sh <dist/SkyMessage-0.2.57.apk> <dist/latest.json>
#       offers the release now: the manifest (latest.json) is replaced with an atomic rename
#   deploy/vps/publish-update.sh --stage <dist/SkyMessage-0.2.57.apk> <dist/latest.json>      (CI)
#       uploads and checks the APK but keeps the manifest as staged-<versionCode>.json: deploy.sh with
#       SKY_BUILD_NUMBER=<versionCode> offers it right after the server of the same release has started, so an old
#       server never offers a client built for the new one
#   deploy/vps/publish-update.sh --withdraw <dist/latest.json>                                 (CI, after a failure)
#       drops the staged manifest and, if this release is already offered, puts the previous manifest back
#
# The manifest comes from deploy/vps/release-manifest.py. Environment as in deploy.sh: SKY_HOST, SKY_SSH_PORT,
# SKY_SSH_KEY, SKY_KNOWN_HOSTS, SKY_REMOTE_DIR.
set -euo pipefail

usage() {
    echo "usage: $0 [--stage] <SkyMessage-x.y.z.apk> <latest.json>" >&2
    echo "       $0 --withdraw <latest.json>" >&2
    exit 2
}

MODE=publish
case "${1:-}" in
    --stage) MODE=stage; shift ;;
    --withdraw) MODE=withdraw; shift ;;
esac
if [ "$MODE" = withdraw ]; then
    [ "$#" -eq 1 ] || usage
    APK=""
    MANIFEST="$1"
else
    [ "$#" -eq 2 ] || usage
    APK="$1"
    MANIFEST="$2"
fi
HOST="${SKY_HOST:-JasperMaglot@77.73.131.184}"
PORT="${SKY_SSH_PORT:-22345}"
KEY="${SKY_SSH_KEY:-$HOME/.ssh/skymessage_vps}"
REMOTE="${SKY_REMOTE_DIR:-skymessage}"
SSH="ssh -o IdentitiesOnly=yes -o IdentityAgent=none -i $KEY -p $PORT"
if [ -n "${SKY_KNOWN_HOSTS:-}" ]; then
    SSH="$SSH -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$SKY_KNOWN_HOSTS"
fi

read -r listed sha code < <(python3 -I -c 'import json, sys; a = json.load(open(sys.argv[1], encoding="utf-8"))["android"]; print(a["file"], a["sha256"], int(a["versionCode"]))' "$MANIFEST")
case "$sha" in
    *[!0-9a-f]* | "") echo "the manifest has no valid SHA-256" >&2; exit 1 ;;
esac
case "$code" in
    *[!0-9]* | "" | 0) echo "the manifest has no valid versionCode" >&2; exit 1 ;;
esac

if [ "$MODE" = withdraw ]; then
    echo "== withdraw release $code"
    $SSH "$HOST" "sh -s -- '$REMOTE/updates' '$sha' '$code'" <<'REMOTE_SCRIPT'
set -eu
[ -d "$1" ] || exit 0
cd "$1"
rm -f -- "staged-$3.json"
if [ -f latest.json ] && grep -qF -- "$2" latest.json; then
    if [ -f latest.json.prev ]; then
        mv -f latest.json.prev latest.json
        echo "the previous release is offered again"
    else
        rm -f latest.json
        echo "no release is offered (there was none before)"
    fi
else
    echo "release $3 was not offered"
fi
REMOTE_SCRIPT
    exit 0
fi

name="$(basename "$APK")"
case "$name" in
    SkyMessage-*.apk) ;;
    *) echo "unexpected APK name: $name (expected SkyMessage-<version>.apk)" >&2; exit 1 ;;
esac
# The manifest must describe exactly this file.
if [ "$listed" != "$name" ]; then
    echo "the manifest names $listed, not $name" >&2
    exit 1
fi
if command -v sha256sum >/dev/null; then local_sha="$(sha256sum "$APK" | cut -d' ' -f1)"; else local_sha="$(shasum -a 256 "$APK" | cut -d' ' -f1)"; fi
if [ "$local_sha" != "$sha" ]; then
    echo "the manifest's SHA-256 does not match $APK" >&2
    exit 1
fi
if [ "$MODE" = stage ]; then target="staged-$code.json"; else target="latest.json"; fi

echo "== upload $name"
$SSH "$HOST" "mkdir -p $REMOTE/updates"
rsync -az --partial -e "$SSH" "$APK" "$HOST:$REMOTE/updates/$name.part"
rsync -az -e "$SSH" "$MANIFEST" "$HOST:$REMOTE/updates/manifest.json.part"

echo "== verify and $MODE"
$SSH "$HOST" "sh -s -- '$REMOTE/updates' '$name' '$sha' '$target'" <<'REMOTE_SCRIPT'
set -eu
cd "$1"
actual=$(sha256sum "$2.part" | cut -d' ' -f1)
if [ "$actual" != "$3" ]; then
    echo "SHA-256 mismatch after the upload; nothing published" >&2
    rm -f -- "$2.part" manifest.json.part
    exit 1
fi
chmod 644 "$2.part" manifest.json.part
mv -f -- "$2.part" "$2"
# The APK is complete before a manifest names it.
if [ "$4" = latest.json ]; then
    if [ -f latest.json ] && ! cmp -s latest.json manifest.json.part; then cp -p latest.json latest.json.prev; fi
    mv -f manifest.json.part latest.json
    echo "offered"
else
    # Only the newest staged release can go live; an older one left by a failed run never will.
    for old in staged-*.json; do
        [ "$old" = "$4" ] || rm -f -- "$old"
    done
    mv -f manifest.json.part "$4"
    echo "staged as $4 (deploy.sh offers it once the server of this release runs)"
fi
ls -1t -- SkyMessage-*.apk | tail -n +4 | while IFS= read -r old; do
    [ "$old" = "$2" ] || rm -f -- "$old"
done
ls -l
REMOTE_SCRIPT
