#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -f "$project_dir/artifacts/native/ArubaMail.dll" ]]; then
  "$project_dir/scripts/build-native.sh"
fi
if [[ "$(uname -s)" == Darwin && ! -f "$project_dir/artifacts/apple/Mail Companion Reminders.app/Contents/MacOS/MailCompanionReminders" ]]; then
  "$project_dir/scripts/build-apple-reminders.sh"
fi
exec "$project_dir/scripts/run-native.sh" --setup --open-browser "$@"
