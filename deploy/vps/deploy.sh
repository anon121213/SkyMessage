#!/usr/bin/env bash
# Deploys SkyMessage to the shared VPS: publishes the API (with the web client) here, rsyncs it with the stack files
# to ~/skymessage on the server and (re)builds and starts the compose project there. Safe to re-run for updates.
#   deploy/vps/deploy.sh            # everything
#   deploy/vps/deploy.sh --no-edge  # db, livekit and api only (before cloudflare.env exists)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HOST="${SKY_HOST:-JasperMaglot@77.73.131.184}"
PORT="${SKY_SSH_PORT:-22345}"
KEY="${SKY_SSH_KEY:-$HOME/.ssh/skymessage_vps}"
REMOTE="${SKY_REMOTE_DIR:-skymessage}"
DOMAIN="${SKY_DOMAIN:-chat.skye.estate}"
NODE_IP="${SKY_NODE_IP:-77.73.131.184}"
SSH="ssh -o IdentitiesOnly=yes -o IdentityAgent=none -i $KEY -p $PORT"
OUT="$ROOT/artifacts/vps"
SERVICES=""
[ "${1:-}" = "--no-edge" ] && SERVICES="db livekit api"

export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

echo "== publish API + web client"
rm -rf "$OUT/app"
mkdir -p "$OUT/bin"
dotnet publish "$ROOT/src/Server/SkyMessage.Api/SkyMessage.Api.csproj" -c Release -o "$OUT/app" -p:UseAppHost=false --nologo -v q
# Portable publish carries native libraries for every OS (~430 MB); the server is linux-x64 (glibc). Publishing with
# -r linux-x64 instead would leak the RID into the WebAssembly client project.
find "$OUT/app/runtimes" -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name unix -exec rm -rf {} +
rm -rf "$OUT/app/BlazorDebugProxy"

if [ ! -f "$OUT/bin/caddy" ]; then
    echo "== cross-build Caddy with the Cloudflare DNS module (linux/amd64)"
    # XCADDY_SETCAP=0: no setcap on the output (fails on a macOS mount; :8443 needs no bind capability).
    docker run --rm -e GOOS=linux -e GOARCH=amd64 -e XCADDY_SETCAP=0 -v "$OUT/bin:/out" caddy:2-builder \
        xcaddy build --output /out/caddy --with github.com/caddy-dns/cloudflare
fi

echo "== upload"
$SSH "$HOST" "mkdir -p $REMOTE/app $REMOTE/bin"
# --checksum: a fresh publish rewrites every file, but only changed content needs uploading.
rsync -az --checksum --delete -e "$SSH" "$OUT/app/" "$HOST:$REMOTE/app/"
rsync -az -e "$SSH" "$OUT/bin/caddy" "$HOST:$REMOTE/bin/caddy"
rsync -az -e "$SSH" "$ROOT"/deploy/vps/{docker-compose.yml,Caddyfile,livekit.yaml,Dockerfile.api,Dockerfile.caddy,init-env.sh,backup.sh} "$HOST:$REMOTE/"

echo "== start on the server"
$SSH "$HOST" "cd $REMOTE && SKY_DOMAIN=$DOMAIN NODE_IP=$NODE_IP sh init-env.sh \
    && docker compose build $SERVICES && docker compose up -d --remove-orphans $SERVICES && docker compose ps"
