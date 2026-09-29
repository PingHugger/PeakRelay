#!/usr/bin/env bash
# prepare-installer-payload.sh — stages everything the GUI installers embed:
#   tools/payload/bepinex.zip              BepInEx 5.4.23.2 (win_x64)
#   tools/payload/plugins/Dedicated/*.dll  dedicated-host plugin + protocol
#   tools/payload/plugins/Client/*.dll     client plugin + protocol
#   tools/payload/relay/*                  published PeakRelay.Server (framework-dependent)
# Run before building PeakRelay.Installer.* — the installer csprojes embed tools/payload/**
# as EmbeddedResource; missing files fail the build (stale payloads cannot ship).
#
# Usage: scripts/prepare-installer-payload.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-$ROOT/tools/dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet)"
[ -n "$DOTNET" ] || { echo "no dotnet found (tools/dotnet or PATH)" >&2; exit 1; }
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1

PAYLOAD="$ROOT/tools/payload"
rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD/plugins/Dedicated" "$PAYLOAD/plugins/Client"

echo ">> fetching BepInEx 5.4.23.2 (win_x64)"
curl -sL -o "$PAYLOAD/bepinex.zip" \
    "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
unzip -q -t "$PAYLOAD/bepinex.zip"   # fail early on a truncated download

echo ">> packing plugin DLLs (embedded as zips, one resource per plugin)"
cp PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Dedicated.dll \
   PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Dedicated/"
cp PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Client.dll \
   PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Client/"

zipdir() {  # zipdir <dest-zip> <src-dir> — PowerShell fallback (no zip binary needed)
    powershell -NoProfile -Command "Compress-Archive -Path '$2' -DestinationPath '$1' -Force"
}
rm -rf "$PAYLOAD/plugins/Dedicated.zip" "$PAYLOAD/plugins/Client.zip" "$PAYLOAD/relay.zip"
zipdir "$(cygpath -w "$PAYLOAD/plugins/Dedicated.zip")" "$(cygpath -w "$PAYLOAD/plugins/Dedicated")"
zipdir "$(cygpath -w "$PAYLOAD/plugins/Client.zip")" "$(cygpath -w "$PAYLOAD/plugins/Client")"

echo ">> publishing relay (framework-dependent, portable)"
"$DOTNET" publish PeakRelay.Server/PeakRelay.Server.csproj -c Release -f net8.0 \
    -o "$PAYLOAD/relay" --nologo -v q
zipdir "$(cygpath -w "$PAYLOAD/relay.zip")" "$(cygpath -w "$PAYLOAD/relay")"

echo ">> payload ready: $PAYLOAD"
find "$PAYLOAD" -type f | sed "s|$ROOT/||" | sort | while read -r f; do echo "   $f"; done
