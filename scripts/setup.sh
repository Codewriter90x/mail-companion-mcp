#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -f "$project_dir/artifacts/native/ArubaMail.dll" ]]; then
  "$project_dir/scripts/build-native.sh"
fi
exec "$project_dir/scripts/run-native.sh" --setup --open-browser "$@"
