#!/usr/bin/env bash
# Regenerates references/ from the shipped game assemblies (kept on disk for recon, never
# committed: decompiled proprietary code must not be redistributed).
#
# Usage: scripts/decompile.sh [path-to-PEAK-install]
# Requires: dotnet with the ilspycmd tool installed (see README)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANAGED="${1:-$ROOT/../PEAK_Data/Managed}"
OUT="$ROOT/references"

mkdir -p "$OUT"
for asm in Photon3Unity3D PhotonUnityNetworking PhotonRealtime Assembly-CSharp; do
    echo ">> decompiling $asm"
    ilspycmd -p -o "$OUT/$asm-src" "$MANAGED/$asm.dll"
done
echo ">> done: $OUT"
