#!/usr/bin/env bash
# peak-server.sh — install + run a headless PEAK dedicated server (PeakRelay M4)
#
# Usage:
#   scripts/peak-server.sh install   # one-time: BepInEx + plugin + server.json template
#   scripts/peak-server.sh run       # launch PEAK headless (-batchmode -nographics)
#   scripts/peak-server.sh status    # show relay link + room state from the plugin log
#
# Config: BepInEx/plugins/PeakRelay.Dedicated/server.json, overridden by env vars
#   PEAKRELAY_ROOM, PEAKRELAY_MAXPLAYERS, PEAKRELAY_HOST, PEAKRELAY_PORT,
#   PEAKRELAY_AUTOHOST, PEAKRELAY_VISIBLE, PEAKRELAY_OPEN, PEAKRELAY_LOGDATAGRAMS
#
# The relay server itself (PeakRelay.Server) is a separate process; start it first,
# e.g. `dotnet PeakRelay.Server/bin/Debug/net8.0/PeakRelay.Server.dll 5055 5056`.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"                 # PeakRelay/
GAME_DIR="${PEAK_GAME_DIR:-$(cd "$ROOT/.." && pwd)}"     # PEAK install root (repo parent)
BEPINEX_DIR="$GAME_DIR/BepInEx"
PLUGIN_DIR="$BEPINEX_DIR/plugins/PeakRelay.Dedicated"
BEPINEX_VERSION="5.4.23.2"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
PLUGIN_DLL="$ROOT/PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Dedicated.dll"
PROTO_DLL="$ROOT/PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll"

die() { echo "peak-server: $*" >&2; exit 1; }

install_plugin() {
    if [ ! -d "$BEPINEX_DIR" ]; then
        echo ">> installing BepInEx $BEPINEX_VERSION into $GAME_DIR"
        TMP="$(mktemp -d)"
        trap 'rm -rf "$TMP"' EXIT
        curl -sL -o "$TMP/bepinex.zip" "$BEPINEX_URL"
        unzip -qo "$TMP/bepinex.zip" -d "$GAME_DIR"
    else
        echo ">> BepInEx already present"
    fi

    [ -f "$PLUGIN_DLL" ] || die "plugin not built: run scripts/fetch-libs.sh, then dotnet build PeakRelay.Dedicated"
    mkdir -p "$PLUGIN_DIR"
    cp "$PLUGIN_DLL" "$PLUGIN_DIR/"
    cp "$PROTO_DLL" "$PLUGIN_DIR/"

    if [ ! -f "$PLUGIN_DIR/server.json" ]; then
        cat > "$PLUGIN_DIR/server.json" <<'JSON'
{
  "roomName": "DBPEAK",
  "maxPlayers": 20,
  "visible": true,
  "open": true,
  "relayHost": "127.0.0.1",
  "relayPort": 5055,
  "autoHost": true,
  "useVanillaName": false,
  "logDatagrams": false
}
JSON
        echo ">> wrote default $PLUGIN_DIR/server.json (edit to taste)"
    fi
    echo ">> plugin installed in $PLUGIN_DIR"
}

run_server() {
    [ -f "$PLUGIN_DIR/PeakRelay.Dedicated.dll" ] || die "plugin not installed: run 'peak-server.sh install' first"
    [ -f "$GAME_DIR/PEAK.exe" ] || die "PEAK.exe not found in $GAME_DIR"
    cd "$GAME_DIR"
    echo ">> launching PEAK headless (env overrides pass through to the plugin)"
    exec ./PEAK.exe -batchmode -nographics -logFile "$BEPINEX_DIR/LogOutput.log"
}

show_status() {
    LOG="$PLUGIN_DIR/server.log"
    [ -f "$LOG" ] || die "no plugin log yet: $LOG"
    echo "== last 25 plugin log lines ($LOG) =="
    tail -25 "$LOG"
}

case "${1:-run}" in
    install) install_plugin ;;
    run)     run_server ;;
    status)  show_status ;;
    *)       die "unknown command '$1' (use install|run|status)" ;;
esac
