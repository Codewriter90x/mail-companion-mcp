#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -f "$project_dir/artifacts/native/ArubaMail.dll" ]]; then
  printf 'Prima eseguire scripts/build-native.sh.\n' >&2
  exit 1
fi
export ARUBA_MAIL_ROOT="${ARUBA_MAIL_ROOT:-$project_dir}"
# No builds or console output on stdio: stdout belongs exclusively to MCP.
exec "${ARUBA_MAIL_DOTNET:-dotnet}" "$project_dir/artifacts/native/ArubaMail.dll" "$@"
