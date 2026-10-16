#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
config_file="${ARUBA_MAIL_CONFIG:-$project_dir/accounts.local.json}"
image_name="${ARUBA_MAIL_IMAGE:-aruba-mail-mcp:local}"
if [[ ! -f "$config_file" ]]; then
  printf 'Configurazione assente. Copia accounts.example.json in accounts.local.json, compila e applica chmod 600.\n' >&2
  exit 1
fi
config_dir="$(cd -- "$(dirname -- "$config_file")" && pwd)"
config_file="$config_dir/$(basename -- "$config_file")"
# Match the file owner's host identity; secrets remain a read-only runtime mount.
exec docker run --interactive --rm \
  --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --cap-drop ALL --security-opt no-new-privileges \
  --user "$(id -u):$(id -g)" \
  --mount "type=bind,source=$config_file,target=/app/accounts.local.json,readonly" \
  "$image_name"
