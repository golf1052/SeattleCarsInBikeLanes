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

package="com.golf1052.SeattleCarsInBikeLanes.Mobile"
original_mode="$("$adb_path" shell cmd window user-rotation)"
original_rotation="$("$adb_path" shell settings get system user_rotation)"
density="$("$adb_path" shell wm density | tail -1 | awk '{print $NF}')"
if [[ ! "$density" =~ ^[0-9]+$ ]]; then
    echo "Could not read the device display density." >&2
    exit 1
fi

restore_rotation() {
    "$adb_path" shell cmd window user-rotation lock "$original_rotation"
    if [[ "$original_mode" == "free" ]]; then
        "$adb_path" shell cmd window user-rotation free
    fi
}
trap restore_rotation EXIT

component="$("$adb_path" shell cmd package resolve-activity --brief "$package" | tail -1)"
"$adb_path" shell am start -W -n "$component" >/dev/null

menu_pattern='BottomNavigationMenuView\{[^}]* ([0-9]+),([0-9]+)-([0-9]+),([0-9]+)'
bar_pattern='BottomNavigationView\{[^}]* ([0-9]+),([0-9]+)-([0-9]+),([0-9]+)'
inset_pattern='type=navigationBars frame=\[([0-9]+),([0-9]+)\]\[([0-9]+),([0-9]+)\]'

# Do not switch tabs between rotations: resizing must work without an appearance refresh.
for rotation in 0 1 3 0 1 0; do
    expected_dp=56
    if [[ "$rotation" == 0 ]]; then
        expected_dp=80
    fi
    expected_px=$(( (expected_dp * density + 80) / 160 ))
    "$adb_path" shell cmd window user-rotation lock "$rotation"

    actual_px=-1
    for attempt in {1..15}; do
        sleep 1
        hierarchy="$("$adb_path" shell dumpsys activity "$package")"
        if [[ "$hierarchy" =~ $menu_pattern ]]; then
            actual_px=$(( BASH_REMATCH[4] - BASH_REMATCH[2] ))
            if [[ "$actual_px" == "$expected_px" ]]; then
                break
            fi
        fi
    done

    if [[ "$actual_px" != "$expected_px" ]]; then
        echo "Rotation $rotation: expected ${expected_px}px (${expected_dp}dp), got ${actual_px}px." >&2
        exit 1
    fi
    if [[ ! "$hierarchy" =~ $bar_pattern ]]; then
        echo "Could not find the native bottom navigation bar." >&2
        exit 1
    fi
    bar_height=$(( BASH_REMATCH[4] - BASH_REMATCH[2] ))

    insets="$("$adb_path" shell dumpsys window displays)"
    if [[ ! "$insets" =~ $inset_pattern ]]; then
        echo "Could not find the system navigation inset." >&2
        exit 1
    fi
    inset_height=$(( BASH_REMATCH[4] - BASH_REMATCH[2] ))
    inset_width=$(( BASH_REMATCH[3] - BASH_REMATCH[1] ))
    if (( inset_height > inset_width )); then
        # Three-button navigation can move to a side in landscape.
        inset_height=0
    fi
    if (( bar_height != actual_px + inset_height )); then
        echo "Rotation $rotation: bar height ${bar_height}px does not preserve the ${inset_height}px system inset." >&2
        exit 1
    fi
    echo "Rotation $rotation: ${expected_dp}dp tabs + ${inset_height}px system inset."
done
