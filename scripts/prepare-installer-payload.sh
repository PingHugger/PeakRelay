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
# Usage: scripts/prepare-installer-payload.sh   (env: DOTNET, BUILD_CONFIG, default Debug)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-$ROOT/tools/dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet)"
[ -n "$DOTNET" ] || { echo "no dotnet found (tools/dotnet or PATH)" >&2; exit 1; }
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1

# Config whose bin/ the plugin DLLs are copied from (release.yml stages the payload
# AFTER building plugins Release, and passes BUILD_CONFIG=Release).
BUILD_CONFIG="${BUILD_CONFIG:-Debug}"

PAYLOAD="$ROOT/Installer.Core/Payload/payload"
rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD/plugins/Dedicated" "$PAYLOAD/plugins/Client"

# CI runners may lack unzip (Git Bash usually ships it); PowerShell covers the rest.
have_unzip() { command -v unzip > /dev/null 2>&1; }
verify_zip() {   # verify_zip <file> — fail early on a truncated/corrupt download
    if have_unzip; then
        unzip -q -t "$1" > /dev/null
    else
        powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; \
            \$z = [IO.Compression.ZipFile]::OpenRead('$(cygpath -w "$1")'); \
            if (\$z.Entries.Count -lt 1) { exit 1 }; \$z.Dispose()"
    fi
}
extract_whole() {  # extract_whole <zip> <destdir>
    if have_unzip; then
        unzip -q -o "$1" -d "$2"
    else
        powershell -NoProfile -Command "Expand-Archive -Path '$(cygpath -w "$1")' -DestinationPath '$(cygpath -w "$2")' -Force"
    fi
}

echo ">> fetching BepInEx 5.4.23.2 (win_x64)"
curl -sL -o "$PAYLOAD/bepinex.zip" \
    "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
verify_zip "$PAYLOAD/bepinex.zip"

# Doorstop 4.5.0 loader: BepInEx 5.4.23.2 bundles Doorstop 4.3.0 (2024), whose proxy DLL
# crashes the 2026-09 PEAK update (native crash in the injected winhttp.dll before BepInEx
# logs anything). The installer always overwrites the game's winhttp.dll with this build.
echo ">> fetching Doorstop 4.5.0 (win_x64 loader)"
curl -sL -o /tmp/doorstop.zip \
    "https://github.com/NeighTools/UnityDoorstop/releases/download/v4.5.0/doorstop_win_release_4.5.0.zip"
rm -rf /tmp/doorstop
extract_whole /tmp/doorstop.zip /tmp/doorstop
mkdir -p "$PAYLOAD/doorstop"
cp /tmp/doorstop/x64/winhttp.dll "$PAYLOAD/doorstop/winhttp.dll"

echo ">> copying plugin DLLs (embedded loose, one resource per DLL)"
cp PeakRelay.Dedicated/bin/$BUILD_CONFIG/netstandard2.1/PeakRelay.Dedicated.dll \
   PeakRelay.Dedicated/bin/$BUILD_CONFIG/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Dedicated/"
cp PeakRelay.Client/bin/$BUILD_CONFIG/netstandard2.1/PeakRelay.Client.dll \
   PeakRelay.Client/bin/$BUILD_CONFIG/netstandard2.1/PeakRelay.Protocol.dll \
   "$PAYLOAD/plugins/Client/"

echo ">> publishing relay (framework-dependent, portable)"
"$DOTNET" publish PeakRelay.Server/PeakRelay.Server.csproj -c Release -f net8.0 \
    -o "$PAYLOAD/relay" --nologo -v q
powershell -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$PAYLOAD/relay")' -DestinationPath '$(cygpath -w "$PAYLOAD/relay.zip")' -Force"

echo ">> payload ready:"
find "$PAYLOAD" -type f | sed "s|$ROOT/||" | sort
