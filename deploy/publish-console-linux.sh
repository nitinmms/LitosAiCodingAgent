#!/usr/bin/env bash
# Publishes Litos.Console as a self-contained, single-file Linux executable.
# Usage: deploy/publish-console-linux.sh [linux-x64|linux-arm64]
set -euo pipefail

RUNTIME="${1:-linux-x64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Litos.Console/Litos.Console.csproj"
BIN_NAME="Litos.Console"
VERSION="${LITOS_VERSION:-1.0.0}"

OUT_DIR="$REPO_ROOT/deploy/out/console/linux/$RUNTIME"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    -o "$OUT_DIR" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION"

chmod +x "$OUT_DIR/$BIN_NAME"

TAR_PATH="$REPO_ROOT/deploy/out/console/linux/Litos.Console-$RUNTIME.tar.gz"
rm -f "$TAR_PATH"
tar -C "$OUT_DIR" -czf "$TAR_PATH" "$BIN_NAME"

echo ""
echo "Published to: $OUT_DIR"
echo "Release archive: $TAR_PATH"
echo "Run: $OUT_DIR/$BIN_NAME"
