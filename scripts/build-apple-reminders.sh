#!/usr/bin/env bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$(uname -s)" != Darwin ]]; then printf 'Il componente Promemoria richiede macOS 14 o successivo.\n' >&2; exit 1; fi
bundle="$project_dir/artifacts/apple/Mail Companion Reminders.app"
mkdir -p "$bundle/Contents/MacOS" "$project_dir/artifacts/swift-cache"
xcrun swiftc -O -target "$(uname -m)-apple-macos14.0" -module-cache-path "$project_dir/artifacts/swift-cache" -framework AppKit -framework EventKit "$project_dir/native/AppleReminders/main.swift" -o "$bundle/Contents/MacOS/MailCompanionReminders"
cp "$project_dir/native/AppleReminders/Info.plist" "$bundle/Contents/Info.plist"
codesign --force --sign - --identifier it.mailcompanion.reminders "$bundle"
printf 'Componente Promemoria compilato. Il consenso si richiede dal pannello locale.\n'
