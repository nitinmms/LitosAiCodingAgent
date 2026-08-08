#!/usr/bin/env bash
# Publishes Litos.Gui as a self-contained macOS .app bundle.
# Must be run on macOS (or a runner capable of producing osx-* builds).
# Usage: deploy/publish-macos.sh [osx-arm64|osx-x64]
#
# Optional signing + notarization (skipped if APPLE_SIGN_IDENTITY is unset):
#   APPLE_SIGN_IDENTITY   "Developer ID Application: Name (TEAMID)"
#   APPLE_ID              Apple ID email, required to notarize
#   APPLE_TEAM_ID         10-char team ID, required to notarize
#   APPLE_APP_PASSWORD    app-specific password, required to notarize
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

if [ -z "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo ""
    echo "Bundled (unsigned): $BUNDLE_DIR"
    echo "Run: open \"$BUNDLE_DIR\""
    echo ""
    echo "Unsigned build - first launch requires right-click > Open (or: xattr -cr \"$BUNDLE_DIR\")"
    exit 0
fi

echo "Signing with identity: $APPLE_SIGN_IDENTITY"
codesign --force --deep --options runtime \
    --entitlements "$REPO_ROOT/deploy/entitlements.plist" \
    --sign "$APPLE_SIGN_IDENTITY" \
    "$BUNDLE_DIR"

ZIP_PATH="$REPO_ROOT/deploy/out/macos/$RUNTIME/$APP_NAME-$RUNTIME.zip"
rm -f "$ZIP_PATH"
ditto -c -k --keepParent "$BUNDLE_DIR" "$ZIP_PATH"

if [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
    echo "Submitting for notarization..."
    xcrun notarytool submit "$ZIP_PATH" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait

    echo "Stapling notarization ticket..."
    xcrun stapler staple "$BUNDLE_DIR"

    rm -f "$ZIP_PATH"
    ditto -c -k --keepParent "$BUNDLE_DIR" "$ZIP_PATH"
else
    echo "Note: APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD not set, skipping notarization."
fi

echo ""
echo "Signed bundle: $BUNDLE_DIR"
echo "Release zip:   $ZIP_PATH"
