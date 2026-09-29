#!/usr/bin/env bash
# Provisions lib/ (reference assemblies for building PeakRelay.Client and
# PeakRelay.Dedicated) without redistributing any game or mod-framework files.
#
# Usage: scripts/fetch-libs.sh [path-to-PEAK-install]
# Requires: curl, unzip
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIB="$ROOT/lib"

# The repo may live anywhere; find the game's Managed dir (arg > PEAK_GAME_DIR >
# default: repo sibling of the Steam install).
find_managed() {
    if [ -n "${1:-}" ]; then echo "$1"; return; fi
    if [ -n "${PEAK_GAME_DIR:-}" ]; then echo "$PEAK_GAME_DIR/PEAK_Data/Managed"; return; fi
    for g in "$ROOT/../PEAK_Data/Managed" \
             "$HOME/../Program Files (x86)/Steam/steamapps/common/PEAK/PEAK_Data/Managed" \
             "/c/Program Files (x86)/Steam/steamapps/common/PEAK/PEAK_Data/Managed"; do
        [ -d "$g" ] && { echo "$g"; return; }
    done
    echo "PEAK Managed dir not found — pass the install path as argument 1 or set PEAK_GAME_DIR" >&2
    exit 1
}
PEAK_MANAGED="$(find_managed "${1:-}")"
BEPINEX_VERSION="5.4.23.2"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"

mkdir -p "$LIB"

echo ">> copying game reference DLLs from $PEAK_MANAGED"
for dll in Photon3Unity3D.dll PhotonRealtime.dll PhotonUnityNetworking.dll Photon.dll Zorro.UI.Runtime.dll \
           UnityEngine.dll UnityEngine.CoreModule.dll UnityEngine.UI.dll UnityEngine.TextRenderingModule.dll UnityEngine.IMGUIModule.dll \
           UnityEngine.MultiplayerModule.dll \
           Assembly-CSharp.dll Zorro.Core.Runtime.dll Utilities.dll Platforms.dll Newtonsoft.Json.dll \
           com.rlabrecque.steamworks.net.dll SteamCommon.dll; do
    cp "$PEAK_MANAGED/$dll" "$LIB/"
done

echo ">> fetching BepInEx $BEPINEX_VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -sL -o "$TMP/bepinex.zip" "$BEPINEX_URL"
unzip -qo "$TMP/bepinex.zip" -d "$TMP/bepinex"
cp "$TMP/bepinex/BepInEx/core/BepInEx.dll" "$LIB/"
cp "$TMP/bepinex/BepInEx/core/0Harmony.dll" "$LIB/"

echo ">> lib/ ready:"
ls -1 "$LIB"
