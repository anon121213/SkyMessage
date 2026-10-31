#!/usr/bin/env sh
# Runs on the VPS (deploy.sh calls it). Creates .env with fresh secrets on the first deploy and never overwrites it.
# Keep a copy of .env somewhere safe: STORAGE_MASTER_KEY decrypts all stored media, and backups are useless
# without it. Usage: SKY_DOMAIN=chat.example.org NODE_IP=203.0.113.7 [SKY_PORT=8443] sh init-env.sh
set -eu
cd "$(dirname "$0")"
if [ -f .env ]; then
    echo ".env exists, keeping it"
    exit 0
fi
: "${SKY_DOMAIN:?set SKY_DOMAIN}" "${NODE_IP:?set NODE_IP}"
SKY_PORT="${SKY_PORT:-8443}"
umask 077
digits() { od -An -tu8 -N16 /dev/urandom | tr -dc 0-9 | cut -c1-"$1"; }
cat > .env <<EOF
SKY_DOMAIN=$SKY_DOMAIN
PUBLIC_BASE_URL=https://$SKY_DOMAIN:$SKY_PORT
LIVEKIT_NODE_IP=$NODE_IP
POSTGRES_PASSWORD=$(openssl rand -hex 24)
JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n')
STORAGE_MASTER_KEY=$(openssl rand -base64 32 | tr -d '\n')
LIVEKIT_API_KEY=skymessage
LIVEKIT_API_SECRET=$(openssl rand -hex 32)
REGISTRATION_MODE=InviteCode
REGISTRATION_CODE=$(digits 8)
EOF
echo "created .env (save a copy of it somewhere safe)"
