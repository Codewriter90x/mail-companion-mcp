#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
image_name="${ARUBA_MAIL_IMAGE:-aruba-mail-mcp:local}"
docker build --tag "$image_name" "$project_dir"
printf 'Container pronto: %s\n' "$image_name"
