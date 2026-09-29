#!/usr/bin/env bash
# prepare-installer-payload.sh — stages everything Installer.Core embeds:
#   Installer.Core/Payload/payload/bepinex.zip              BepInEx 5.4.23.2 (win_x64)
#   Installer.Core/Payload/payload/plugins/Dedicated/*.dll  dedicated plugin DLLs
#   Installer.Core/Payload/payload/plugins/Client/*.dll     client plugin DLLs
#   Installer.Core/Payload/payload/relay/relay.zip          published PeakRelay.Server
#
# Plugin DLLs are embedded LOOSE (one resource per DLL — no zip-in-zip layer); the relay
# publish output ships as one zip because it is many files staged as-is. Installer.Core
# embeds with path-derived LogicalNames (Payload/payload/** -> PeakRelay.Installer.Payload.*),
# and its build FAILS if bepinex.zip is missing, so installers cannot build stale.
# tests/PeakRelay.Protocol.Tests/InstallerPayloadTests.cs re-checks the name contract.
#
# Usage: scripts/prepare-installer-payload.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-$ROOT/tools/dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet)"
[ -n "$DOTNET" ] || { echo "no dotnet found (tools/dotnet or PATH)" >&2; exit 1; }
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1

PAYLOAD="$ROOT/Installer.Core/Payload/payload"
rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD/plugins/Dedicated" "$PAYLOAD/plugins/Client"

echo ">> fetching BepInEx 5.4.23.2 (win_x64)"
curl -sL -o "$PAYLOAD/bepinex.zip" \
    "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
unzip -q -t "$PAYLOAD/bepinex.zip"   # fail early on a truncated download

echo ">> copying plugin DLLs (embedded loose, one resource per DLL)"
cp PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Dedicated.dll \
   PeakRelay.Dedicated/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Dedicated/"
cp PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Client.dll \
   PeakRelay.Client/bin/Debug/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Client/"

echo ">> publishing relay (framework-dependent, portable)"
"$DOTNET" publish PeakRelay.Server/PeakRelay.Server.csproj -c Release -f net8.0 \
    -o "$PAYLOAD/relay" --nologo -v q
powershell -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$PAYLOAD/relay")' -DestinationPath '$(cygpath -w "$PAYLOAD/relay.zip")' -Force"

echo ">> payload ready:"
find "$PAYLOAD" -type f | sed "s|$ROOT/||" | sort
