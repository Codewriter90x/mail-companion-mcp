#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet restore "$project_dir/src/ArubaMail" --locked-mode
dotnet publish "$project_dir/src/ArubaMail" --configuration Release --no-restore --output "$project_dir/artifacts/native"
