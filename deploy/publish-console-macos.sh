#!/usr/bin/env bash
# Publishes Litos.Console as a self-contained macOS executable — a plain CLI binary, not an
# .app bundle (unlike publish-macos.sh/Litos.Gui): Litos.Console is a Terminal.Gui TUI meant to
# be run from a real terminal, so it has no windowed-app lifecycle, Info.plist, or Dock icon to
# provide, and needs none of Gui's bundle-swap update mechanism either.
# Must be run on macOS (or a runner capable of producing osx-* builds).
# Usage: deploy/publish-console-macos.sh [osx-arm64|osx-x64]
#
# Optional signing + notarization (skipped if APPLE_SIGN_IDENTITY is unset) — recommended even
# for a plain binary, since Gatekeeper still quarantines an unsigned/unnotarized downloaded
# executable and blocks first-run without an explicit right-click > Open bypass:
#   APPLE_SIGN_IDENTITY   "Developer ID Application: Name (TEAMID)"
#   APPLE_ID              Apple ID email, required to notarize
#   APPLE_TEAM_ID         10-char team ID, required to notarize
#   APPLE_APP_PASSWORD    app-specific password, required to notarize
set -euo pipefail

RUNTIME="${1:-osx-arm64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Litos.Console/Litos.Console.csproj"
BIN_NAME="Litos.Console"
VERSION="${LITOS_VERSION:-1.0.0}"

PUBLISH_DIR="$REPO_ROOT/deploy/out/console/macos/$RUNTIME/publish"

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RUNTIME" \
    -o "$PUBLISH_DIR" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION"

chmod +x "$PUBLISH_DIR/$BIN_NAME"

if [ -z "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo ""
    echo "Published (unsigned): $PUBLISH_DIR/$BIN_NAME"
    echo "Unsigned build - first launch requires: xattr -cr \"$PUBLISH_DIR/$BIN_NAME\""
else
    echo "Signing with identity: $APPLE_SIGN_IDENTITY"
    codesign --force --options runtime --sign "$APPLE_SIGN_IDENTITY" "$PUBLISH_DIR/$BIN_NAME"
fi

ZIP_PATH="$REPO_ROOT/deploy/out/console/macos/$RUNTIME/Litos.Console-$RUNTIME.zip"
rm -f "$ZIP_PATH"
ditto -c -k --keepParent "$PUBLISH_DIR/$BIN_NAME" "$ZIP_PATH"

if [ -n "${APPLE_SIGN_IDENTITY:-}" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
    echo "Submitting for notarization..."
    xcrun notarytool submit "$ZIP_PATH" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait

    # Stapling requires the original signed binary, not the zip — staple, then re-zip.
    xcrun stapler staple "$PUBLISH_DIR/$BIN_NAME" || echo "Note: stapling a plain executable (not a bundle) is not always supported; notarization ticket is still valid online."
    rm -f "$ZIP_PATH"
    ditto -c -k --keepParent "$PUBLISH_DIR/$BIN_NAME" "$ZIP_PATH"
else
    echo "Note: APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD not set, skipping notarization."
fi

echo ""
echo "Release zip: $ZIP_PATH"
