#!/usr/bin/env bash
# Asks for the Cloudflare API token (Zone > DNS > Edit, for the domain's zone) and stores it on the VPS in
# ~/skymessage/cloudflare.env (mode 600) for Caddy's ACME DNS challenge. Input is hidden; paste and press Enter.
set -euo pipefail
HOST="${SKY_HOST:-JasperMaglot@77.73.131.184}"
PORT="${SKY_SSH_PORT:-22345}"
KEY="${SKY_SSH_KEY:-$HOME/.ssh/skymessage_vps}"
REMOTE="${SKY_REMOTE_DIR:-skymessage}"

read -rsp "Вставь токен Cloudflare и нажми Enter (символы не видны): " token
# A pasted multi-line `curl ... -H "Authorization: Bearer <token>"` example arrives as several lines: collect them.
while IFS= read -rs -t 1 more; do token="$token $more"; done
echo
# Accept the bare token, the Authorization header, or Cloudflare's whole test command.
case "$token" in
    *Bearer*) token="${token##*Bearer}" ;;
esac
token="$(printf '%s' "$token" | tr -d '"'"'" | awk '{print $1}')"
case "$token" in
    */*|*\\*) token="" ;;
esac
if [ "${#token}" -lt 30 ]; then
    echo "Не похоже на токен, ничего не сохранено. Скопируй сам токен (или пример с Authorization: Bearer)."
    exit 1
fi
[ -n "$token" ] || { echo "Пусто, ничего не сохранено."; exit 1; }
# Saved and verified on the server; the token travels on stdin, never on a command line.
remote="umask 077 && mkdir -p $REMOTE && cat > $REMOTE/cloudflare.env && echo 'Токен сохранён на сервере.'"
remote="$remote && t=\$(sed -n 's/^CF_API_TOKEN=//p' $REMOTE/cloudflare.env)"
remote="$remote && if curl -s https://api.cloudflare.com/client/v4/user/tokens/verify -H \"Authorization: Bearer \$t\" | grep -q '\"status\":\"active\"'; then echo 'Cloudflare: токен действует.'; else echo 'Cloudflare не принял токен: скопируй именно его и запусти скрипт ещё раз.'; fi"
printf 'CF_API_TOKEN=%s\n' "$token" | ssh -o IdentitiesOnly=yes -o IdentityAgent=none -i "$KEY" -p "$PORT" "$HOST" "$remote"
