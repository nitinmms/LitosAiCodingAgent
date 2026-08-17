#!/usr/bin/env bash
# Installs the latest Litos.Console release from GitHub.
# Usage: curl -fsSL https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install-console.sh | bash
#
# Detects macOS vs Linux and architecture, downloads the matching self-contained single-file
# executable from the latest "console-v*.*.*" GitHub Release (release-console.yml — separate
# from Litos.Gui's "v*.*.*" releases), and installs it to ~/.local/bin.
set -euo pipefail

REPO="nitinmms/LitosAiCodingAgent"
INSTALL_DIR="$HOME/.local/bin"
BIN_NAME="Litos.Console"

case "$(uname -s)" in
    Darwin) OS="macos" ;;
    Linux)  OS="linux" ;;
    *)      echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    arm64|aarch64) ARCH="arm64" ;;
    x86_64)        ARCH="x64" ;;
    *)             echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

if [ "$OS" = "macos" ]; then
    RUNTIME="osx-$ARCH"
    ASSET_NAME="Litos.Console-$RUNTIME.zip"
elif [ "$OS" = "linux" ] && [ "$ARCH" = "arm64" ]; then
    RUNTIME="linux-arm64"
    ASSET_NAME="Litos.Console-$RUNTIME.tar.gz"
else
    RUNTIME="linux-x64"
    ASSET_NAME="Litos.Console-$RUNTIME.tar.gz"
fi

echo "Looking up latest console release for $RUNTIME..."

RELEASE_JSON="$(curl -fsSL "https://api.github.com/repos/$REPO/releases" \
    | python3 -c "import json,sys; rs=json.load(sys.stdin); print(next((r['tag_name'] for r in rs if r['tag_name'].startswith('console-v')), ''))" 2>/dev/null || true)"

if [ -z "$RELEASE_JSON" ]; then
    echo "Could not find a console-v* release for $REPO. Has a Litos.Console version been released yet?" >&2
    exit 1
fi

TAG="$RELEASE_JSON"
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$TAG/$ASSET_NAME"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Downloading $ASSET_NAME ($TAG)..."
curl -fsSL "$DOWNLOAD_URL" -o "$WORK_DIR/$ASSET_NAME"

mkdir -p "$INSTALL_DIR"

if [ "$OS" = "macos" ]; then
    ditto -x -k "$WORK_DIR/$ASSET_NAME" "$WORK_DIR"
    mv "$WORK_DIR/$BIN_NAME" "$INSTALL_DIR/$BIN_NAME"
    chmod +x "$INSTALL_DIR/$BIN_NAME"
    xattr -cr "$INSTALL_DIR/$BIN_NAME" 2>/dev/null || true
else
    tar -xzf "$WORK_DIR/$ASSET_NAME" -C "$WORK_DIR"
    mv "$WORK_DIR/$BIN_NAME" "$INSTALL_DIR/$BIN_NAME"
    chmod +x "$INSTALL_DIR/$BIN_NAME"
fi

echo ""
echo "Litos.Console $TAG installed to $INSTALL_DIR/$BIN_NAME"
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    echo "Note: $INSTALL_DIR is not on your PATH. Add this to your shell profile:"
    echo "  export PATH=\"\$PATH:$INSTALL_DIR\""
fi
echo "Run: $BIN_NAME"
