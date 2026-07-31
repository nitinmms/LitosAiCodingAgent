#!/usr/bin/env bash
# Publishes Litos.Gui as a self-contained macOS .app bundle.
# Must be run on macOS (or a runner capable of producing osx-* builds).
# Usage: deploy/publish-macos.sh [osx-arm64|osx-x64]
set -euo pipefail

RUNTIME="${1:-osx-arm64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Litos.Gui/Litos.Gui.csproj"
APP_NAME="Litos"
VERSION="${LITOS_VERSION:-1.0.0}"

PUBLISH_DIR="$REPO_ROOT/deploy/out/macos/$RUNTIME/publish"
BUNDLE_DIR="$REPO_ROOT/deploy/out/macos/$RUNTIME/$APP_NAME.app"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    -o "$PUBLISH_DIR" \
    --self-contained true \
    -p:PublishSingleFile=true

rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/Contents/MacOS"
mkdir -p "$BUNDLE_DIR/Contents/Resources"

cp -R "$PUBLISH_DIR/." "$BUNDLE_DIR/Contents/MacOS/"
chmod +x "$BUNDLE_DIR/Contents/MacOS/Litos.Gui"

sed "s/__VERSION__/$VERSION/g" "$REPO_ROOT/deploy/Info.plist.template" > "$BUNDLE_DIR/Contents/Info.plist"

ICONSET_SRC="$REPO_ROOT/deploy/AppIcon.iconset"
if [ -d "$ICONSET_SRC" ]; then
    iconutil -c icns "$ICONSET_SRC" -o "$BUNDLE_DIR/Contents/Resources/AppIcon.icns"
else
    echo "Note: deploy/AppIcon.iconset not found, bundling without a custom icon."
fi

echo ""
echo "Bundled: $BUNDLE_DIR"
echo "Run: open \"$BUNDLE_DIR\""
echo ""
echo "Unsigned build - first launch requires right-click > Open (or: xattr -cr \"$BUNDLE_DIR\")"
