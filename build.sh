#!/usr/bin/env bash
# Builds Resit for Linux as one self-contained file (users need no .NET install):
# dist/linux/ and dist/resit-linux-x64.tar.gz. Needs the .NET 10 SDK.
#
# Usage: ./build.sh [Release|Debug]
set -euo pipefail

CONFIGURATION=${1:-Release}
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
OUT=$ROOT/dist/linux

command -v dotnet >/dev/null || { echo "Install the .NET 10 SDK: https://dotnet.microsoft.com/download" >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1

dotnet publish "$ROOT/src/Resit/Resit.csproj" -c "$CONFIGURATION" -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "$OUT"

install -m 755 "$ROOT/setup.sh" "$OUT/setup.sh"
install -m 755 "$ROOT/linux/install.sh" "$OUT/install.sh"
install -m 644 "$ROOT/linux/resit.desktop" "$OUT/resit.desktop"
install -m 644 "$ROOT/assets/logo-256.png" "$OUT/resit.png"

(cd "$ROOT/dist" && tar --transform 's|^linux|resit|' -czf resit-linux-x64.tar.gz linux)
echo "Built $ROOT/dist/resit-linux-x64.tar.gz"
echo "Install with: tar xzf dist/resit-linux-x64.tar.gz && ./resit/install.sh"
