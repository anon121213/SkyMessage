#!/usr/bin/env bash
# Deploys SkyMessage to the shared VPS: publishes the API (with the web client) here, rsyncs it with the stack files
# to ~/skymessage on the server and (re)builds and starts the compose project there. Safe to re-run for updates.
# Runs from a Mac/Linux checkout and from CI (.github/workflows/release.yml). It only ever touches ~/skymessage and
# the compose project "skymessage" (its containers, network, volumes and its own previous API image).
#   deploy/vps/deploy.sh            # everything
#   deploy/vps/deploy.sh --no-edge  # db, livekit and api only (before cloudflare.env exists)
# Environment (all optional):
#   SKY_HOST user@host, SKY_SSH_PORT, SKY_SSH_KEY  where and how to connect
#   SKY_KNOWN_HOSTS     a known_hosts file to verify the server against (CI); no prompts then
#   SKY_BUILD_NUMBER    the release build number (version 0.2.<n>, see Directory.Build.props); CI passes it. The
#                       Android release of that number staged by publish-update.sh --stage is offered to the apps
#                       right after this server has started
#   SKY_REBUILD_CADDY=1 build and upload Caddy even though the server already has it
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST="${SKY_HOST:-JasperMaglot@77.73.131.184}"
PORT="${SKY_SSH_PORT:-22345}"
KEY="${SKY_SSH_KEY:-$HOME/.ssh/skymessage_vps}"
REMOTE="${SKY_REMOTE_DIR:-skymessage}"
DOMAIN="${SKY_DOMAIN:-chat.skye.estate}"
NODE_IP="${SKY_NODE_IP:-77.73.131.184}"
SSH="ssh -o IdentitiesOnly=yes -o IdentityAgent=none -i $KEY -p $PORT"
if [ -n "${SKY_KNOWN_HOSTS:-}" ]; then
    SSH="$SSH -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$SKY_KNOWN_HOSTS"
fi
OUT="$ROOT/artifacts/vps"
SERVICES=""
[ "${1:-}" = "--no-edge" ] && SERVICES="db livekit api"
VERSION_ARGS=()
[ -n "${SKY_BUILD_NUMBER:-}" ] && VERSION_ARGS=("-p:SkyMessageBuildNumber=$SKY_BUILD_NUMBER")

export PATH="$HOME/.dotnet:$PATH"
[ -x "$HOME/.dotnet/dotnet" ] && export DOTNET_ROOT="$HOME/.dotnet"

echo "== publish API + web client"
rm -rf "$OUT/app"
mkdir -p "$OUT/bin"
dotnet publish "$ROOT/src/Server/SkyMessage.Api/SkyMessage.Api.csproj" -c Release -o "$OUT/app" -p:UseAppHost=false ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"} --nologo -v q
# Portable publish carries native libraries for every OS (~430 MB); the server is linux-x64 (glibc). Publishing with
# -r linux-x64 instead would leak the RID into the WebAssembly client project.
find "$OUT/app/runtimes" -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name unix -exec rm -rf {} +
rm -rf "$OUT/app/BlazorDebugProxy"

# Caddy is cross-built once. A machine without a local copy (CI) keeps the server's binary: rebuilding it would
# restart the edge on every release for nothing.
UPLOAD_CADDY=1
if [ ! -f "$OUT/bin/caddy" ]; then
    if [ -z "${SKY_REBUILD_CADDY:-}" ] && $SSH "$HOST" "test -s $REMOTE/bin/caddy"; then
        echo "== keeping the server's Caddy"
        UPLOAD_CADDY=0
    else
        echo "== cross-build Caddy with the Cloudflare DNS module (linux/amd64)"
        # XCADDY_SETCAP=0: no setcap on the output (fails on a macOS mount; :8443 needs no bind capability).
        docker run --rm -e GOOS=linux -e GOARCH=amd64 -e XCADDY_SETCAP=0 -v "$OUT/bin:/out" caddy:2-builder \
            xcaddy build --output /out/caddy --with github.com/caddy-dns/cloudflare
    fi
fi

echo "== upload"
# updates/: APKs and their manifest (publish-update.sh), mounted read-only into the API. Created here by this user,
# so Docker never creates it as root.
$SSH "$HOST" "mkdir -p $REMOTE/app $REMOTE/bin $REMOTE/updates"
# --checksum: a fresh publish rewrites every file, but only changed content needs uploading.
rsync -az --checksum --delete -e "$SSH" "$OUT/app/" "$HOST:$REMOTE/app/"
[ "$UPLOAD_CADDY" = 1 ] && rsync -az -e "$SSH" "$OUT/bin/caddy" "$HOST:$REMOTE/bin/caddy"
rsync -az -e "$SSH" "$ROOT"/deploy/vps/{docker-compose.yml,Caddyfile,livekit.yaml,Dockerfile.api,Dockerfile.caddy,init-env.sh,backup.sh} "$HOST:$REMOTE/"

echo "== start on the server"
# Only this project: its compose file, its containers, the API image it replaces and its updates directory. Nothing
# else on the host.
$SSH "$HOST" "sh -s -- '$REMOTE' '$DOMAIN' '$NODE_IP' '$SERVICES' '${SKY_BUILD_NUMBER:-}'" <<'REMOTE_SCRIPT'
set -eu
cd "$1"
SKY_DOMAIN="$2" NODE_IP="$3" sh init-env.sh
old_api=$(docker image inspect -f '{{.Id}}' skymessage-api:prod 2>/dev/null || true)
# $4 (the services) stays unquoted on purpose: empty means all of them.
docker compose build $4
docker compose up -d --remove-orphans $4
# The Android build of this release goes live only now, with its server: the old one never offered it, and the apps
# that reconnect to the new one (still starting, a few seconds) find it at once. A failed "up" stops before this.
if [ -n "$5" ] && [ -f "updates/staged-$5.json" ]; then
    if [ -f updates/latest.json ] && cmp -s updates/latest.json "updates/staged-$5.json"; then
        rm -f -- "updates/staged-$5.json"
    else
        if [ -f updates/latest.json ]; then cp -p updates/latest.json updates/latest.json.prev; fi
        mv -f -- "updates/staged-$5.json" updates/latest.json
    fi
    echo "== the apps are offered release $5"
fi
docker compose ps
new_api=$(docker image inspect -f '{{.Id}}' skymessage-api:prod 2>/dev/null || true)
if [ -n "$old_api" ] && [ "$old_api" != "$new_api" ]; then
    # The API image this deploy replaced (no container uses it any more); keeps the disk from filling up.
    docker image rm "$old_api" >/dev/null 2>&1 || true
fi
REMOTE_SCRIPT
