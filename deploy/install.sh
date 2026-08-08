#!/usr/bin/env bash
# Installs the latest Litos.app release from GitHub into /Applications.
# Usage: curl -fsSL https://raw.githubusercontent.com/nitinmms/LitosAiCodingAgent/master/deploy/install.sh | bash
set -euo pipefail

REPO="nitinmms/LitosAiCodingAgent"
APP_NAME="Litos"

case "$(uname -m)" in
    arm64)  RUNTIME="osx-arm64" ;;
    x86_64) RUNTIME="osx-x64" ;;
    *)      echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

ASSET_NAME="$APP_NAME-$RUNTIME.zip"
echo "Looking up latest release for $RUNTIME..."

DOWNLOAD_URL="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | grep -o "\"browser_download_url\": *\"[^\"]*$ASSET_NAME\"" \
    | sed -E 's/.*"(https:[^"]+)"/\1/')"

if [ -z "$DOWNLOAD_URL" ]; then
    echo "Could not find a release asset named $ASSET_NAME. Has a release been published yet?" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Downloading $DOWNLOAD_URL..."
curl -fsSL "$DOWNLOAD_URL" -o "$WORK_DIR/$ASSET_NAME"

echo "Extracting..."
ditto -x -k "$WORK_DIR/$ASSET_NAME" "$WORK_DIR"

TARGET="/Applications/$APP_NAME.app"
if [ -d "$TARGET" ]; then
    echo "Removing previous install at $TARGET..."
    rm -rf "$TARGET"
fi

echo "Installing to $TARGET..."
mv "$WORK_DIR/$APP_NAME.app" "$TARGET"

echo ""
echo "Done. Launch with: open \"$TARGET\""
