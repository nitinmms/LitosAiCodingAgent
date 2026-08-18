#!/usr/bin/env bash
# Publishes Litos.VsCodeHost as a self-contained, single-file Linux executable — the local
# no-auth agent host the Litos VS Code extension (src/Litos.VsCode) spawns as a child process and
# bundles per-RID under src/Litos.VsCode/bin/<rid>/ (see ReadMe_VsCodeExtension.md §6).
# Usage: deploy/publish-vscodehost-linux.sh [linux-x64|linux-arm64]
set -euo pipefail

RUNTIME="${1:-linux-x64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Litos.VsCodeHost/Litos.VsCodeHost.csproj"
BIN_NAME="Litos.VsCodeHost"
VERSION="${LITOS_VERSION:-1.0.0}"

OUT_DIR="$REPO_ROOT/deploy/out/vscodehost/linux/$RUNTIME"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    -o "$OUT_DIR" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION"

chmod +x "$OUT_DIR/$BIN_NAME"

TAR_PATH="$REPO_ROOT/deploy/out/vscodehost/linux/Litos.VsCodeHost-$RUNTIME.tar.gz"
rm -f "$TAR_PATH"
tar -C "$OUT_DIR" -czf "$TAR_PATH" "$BIN_NAME"

echo ""
echo "Published to: $OUT_DIR"
echo "Release archive: $TAR_PATH"
echo "Copy into the extension bundle with:"
echo "  cp \"$OUT_DIR/$BIN_NAME\" \"src/Litos.VsCode/bin/$RUNTIME/\""
