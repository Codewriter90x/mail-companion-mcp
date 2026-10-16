#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
config_file="${ARUBA_MAIL_CONFIG:-$project_dir/accounts.local.json}"
token_file="${ARUBA_MAIL_MCP_TOKEN_FILE:-$project_dir/mcp.token}"
for file in "$config_file" "$token_file"; do
  if [[ ! -f "$file" ]]; then
    printf 'Configurazione o token assente. Compila accounts.local.json ed esegui scripts/create-http-token.sh.\n' >&2
    exit 1
  fi
done
config_file="$(cd -- "$(dirname -- "$config_file")" && pwd)/$(basename -- "$config_file")"
token_file="$(cd -- "$(dirname -- "$token_file")" && pwd)/$(basename -- "$token_file")"
exec docker run --rm --init \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --cap-drop ALL --security-opt no-new-privileges \
  --user "$(id -u):$(id -g)" \
  --publish "127.0.0.1:${ARUBA_MAIL_HTTP_PORT:-8000}:8000" \
  --env ARUBA_MAIL_MCP_TOKEN_FILE=/run/secrets/mcp.token \
  --env "ARUBA_MAIL_ALLOWED_HOSTS=${ARUBA_MAIL_ALLOWED_HOSTS:-127.0.0.1:*,localhost:*,[::1]:*,host.docker.internal:*,aruba-mail:*}" \
  --mount "type=bind,source=$config_file,target=/app/accounts.local.json,readonly" \
  --mount "type=bind,source=$token_file,target=/run/secrets/mcp.token,readonly" \
  "${ARUBA_MAIL_IMAGE:-aruba-mail-mcp:local}" \
  --transport streamable-http --host 0.0.0.0 --port 8000
