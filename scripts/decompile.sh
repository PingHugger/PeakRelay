#!/usr/bin/env bash
# Regenerates references/ from the shipped game assemblies (kept on disk for recon, never
# committed: decompiled proprietary code must not be redistributed).
#
# Usage: scripts/decompile.sh [path-to-PEAK-install]
# Requires: dotnet with the ilspycmd tool installed (see README)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# The repo may live anywhere; find the game's Managed dir (arg > PEAK_GAME_DIR >
# default: repo sibling of the Steam install).
find_managed() {
    if [ -n "${1:-}" ]; then echo "$1"; return; fi
    if [ -n "${PEAK_GAME_DIR:-}" ] && [ -d "$PEAK_GAME_DIR/PEAK_Data/Managed" ]; then echo "$PEAK_GAME_DIR/PEAK_Data/Managed"; return; fi
    for g in "$ROOT/../PEAK_Data/Managed" \
             "/c/Program Files (x86)/Steam/steamapps/common/PEAK/PEAK_Data/Managed"; do
        [ -d "$g" ] && { echo "$g"; return; }
    done
    echo "PEAK Managed dir not found — pass the install path as argument 1 or set PEAK_GAME_DIR" >&2
    exit 1
}
MANAGED="$(find_managed "${1:-}")"
OUT="$ROOT/references"

mkdir -p "$OUT"
for asm in Photon3Unity3D PhotonUnityNetworking PhotonRealtime Assembly-CSharp; do
    echo ">> decompiling $asm"
    ilspycmd -p -o "$OUT/$asm-src" "$MANAGED/$asm.dll"
done
echo ">> done: $OUT"
