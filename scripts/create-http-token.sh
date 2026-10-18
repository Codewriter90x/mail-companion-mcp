#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
default_config="$project_dir/accounts.local.json"
if [[ -f "$project_dir/config/accounts.local.json" ]]; then default_config="$project_dir/config/accounts.local.json"; fi
config_file="${ARUBA_MAIL_CONFIG:-$default_config}"
token_file="${ARUBA_MAIL_MCP_TOKEN_FILE:-$(dirname -- "$config_file")/mcp.token}"
if [[ -e "$token_file" ]]; then
  printf 'Token già presente: nessuna sovrascrittura.\n' >&2
  exit 1
fi
umask 077
# noclobber prevents races or accidental overwrite; no secret printed.
(set -o noclobber; openssl rand -hex 32 > "$token_file")
printf 'Creato mcp.token con permessi privati. Aprilo localmente per configurare Open WebUI.\n'
