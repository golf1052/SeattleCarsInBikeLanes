#!/usr/bin/env bash

set -euo pipefail

adb_path="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
if [[ ! -x "$adb_path" ]]; then
    adb_path="$(command -v adb || true)"
fi

if [[ -z "$adb_path" ]]; then
    echo "adb was not found. Set ADB to its full path or add it to PATH." >&2
    exit 1
fi

output_directory="$HOME/Downloads"
mkdir -p "$output_directory"

output_path="$output_directory/android-screenshot-$(uuidgen).png"
"$adb_path" exec-out screencap -p > "$output_path"

if [[ ! -s "$output_path" ]]; then
    echo "adb did not return a screenshot." >&2
    exit 1
fi

echo "$output_path"
