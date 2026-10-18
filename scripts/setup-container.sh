#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
config_dir="${ARUBA_MAIL_CONFIG_DIR:-$project_dir/config}"
port="${ARUBA_MAIL_SETUP_PORT:-8765}"
if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1024 || port > 65535)); then
  printf 'Porta pannello non valida.\n' >&2; exit 1
fi
(umask 077; mkdir -p -- "$config_dir")
config_dir="$(cd -- "$config_dir" && pwd)"
container_id=""
cleanup() { if [[ -n "$container_id" ]]; then docker stop "$container_id" >/dev/null 2>&1 || true; fi; }
trap cleanup EXIT INT TERM
container_id="$(docker run --detach --rm --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=16m --cap-drop ALL --security-opt no-new-privileges \
  --user "$(id -u):$(id -g)" \
  --publish "127.0.0.1:$port:8765" \
  --env ARUBA_MAIL_CONFIG=/config/accounts.local.json \
  --env ARUBA_MAIL_PROFILE_PATH=/config/connection.local.json \
  --env ARUBA_MAIL_MCP_TOKEN_FILE=/config/mcp.token \
  --env ARUBA_MAIL_SETUP_CONTAINER=1 \
  --env "ARUBA_MAIL_SETUP_IMAGE=${ARUBA_MAIL_IMAGE:-aruba-mail-mcp:local}" \
  --env "ARUBA_MAIL_SETUP_PROJECT_DIR=$project_dir" \
  --env "ARUBA_MAIL_SETUP_CONFIG_DIR=$config_dir" \
  --mount "type=bind,source=$config_dir,target=/config" \
  "${ARUBA_MAIL_IMAGE:-aruba-mail-mcp:local}" --setup --host 0.0.0.0 --port 8765)"
ready=false
for attempt in {1..100}; do
  if [[ -f "$config_dir/setup.token" ]] && curl --silent --fail --output /dev/null "http://127.0.0.1:$port/"; then ready=true; break; fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container_id" 2>/dev/null || true)" != true ]]; then break; fi
  sleep 0.1
done
if [[ "$ready" != true ]]; then printf 'Pannello non avviato: controllare porta, immagine e permessi della directory config.\n' >&2; exit 1; fi
# The fragment is consumed locally and removed from browser history by the page.
# No password or token is printed; no token is sent in the HTTP URL.
access_code="$(cat -- "$config_dir/setup.token")"
setup_url="http://127.0.0.1:$port/#$access_code"
if command -v open >/dev/null 2>&1; then open "$setup_url"
elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$setup_url"
else printf 'Apri http://127.0.0.1:%s/ e inserisci il codice del file config/setup.token.\n' "$port"
fi
unset access_code setup_url
printf 'Pannello locale attivo. Al termine torna qui e premi Invio per chiuderlo.\n'
read -r _
