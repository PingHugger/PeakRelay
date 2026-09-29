#!/usr/bin/env bash
# publish.sh — release build of PeakRelay.Server (framework-dependent, trimmed-free)
#
# Output: dist/PeakRelay.Server/   → run with: dotnet PeakRelay.Server.dll [tcpPort] [httpPort]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
# Portable SDK first (tools/dotnet), then PATH.
DOTNET="${DOTNET:-$ROOT/tools/dotnet/dotnet.exe}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet)"
[ -n "$DOTNET" ] || { echo "no dotnet found (tools/dotnet or PATH)" >&2; exit 1; }
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1

echo ">> publishing PeakRelay.Server (net8.0, framework-dependent)"
"$DOTNET" publish PeakRelay.Server/PeakRelay.Server.csproj \
    -c Release \
    -f net8.0 \
    -o "$ROOT/dist/PeakRelay.Server" \
    --nologo

echo ">> dist/PeakRelay.Server ready:"
ls -1 "$ROOT/dist/PeakRelay.Server" | head -5
echo ">> run: cd dist/PeakRelay.Server && dotnet PeakRelay.Server.dll 5055 5056"
