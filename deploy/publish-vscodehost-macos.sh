#!/usr/bin/env bash
# Publishes Litos.VsCodeHost as a self-contained macOS executable — a plain headless binary, not
# an .app bundle (unlike publish-macos.sh/Litos.Gui): this process has no window, no Dock icon,
# and no Info.plist/entitlements to provide — it's a background agent host the Litos VS Code
# extension (src/Litos.VsCode) spawns silently as a child process and bundles per-RID under
# src/Litos.VsCode/bin/<rid>/ (see ReadMe_VsCodeExtension.md §6). Structurally identical to the
# now-shelved Litos.Console's own macOS publish script (deploy/publish-console-macos.sh) — that
# script's shape is still the correct reference here even though Litos.Console itself is shelved,
# since Litos.VsCodeHost is the same kind of plain headless binary.
# Must be run on macOS (or a runner capable of producing osx-* builds).
# Usage: deploy/publish-vscodehost-macos.sh [osx-arm64|osx-x64]
#
# Signing + notarization matters MORE here than for a directly-launched CLI: Gatekeeper quarantines
# any downloaded, unsigned executable and blocks first-run — but since this binary is spawned
# silently by the extension (never double-clicked or run from a terminal by the user directly),
# there's no natural moment for the user to see Gatekeeper's "Open anyway" dialog and unblock it
# themselves. An unsigned build here fails opaquely on first "Litos: Open Chat" activation.
# Optional (skipped if APPLE_SIGN_IDENTITY is unset), but should be set for any real release:
#   APPLE_SIGN_IDENTITY   "Developer ID Application: Name (TEAMID)"
#   APPLE_ID              Apple ID email, required to notarize
#   APPLE_TEAM_ID         10-char team ID, required to notarize
#   APPLE_APP_PASSWORD    app-specific password, required to notarize
set -euo pipefail

RUNTIME="${1:-osx-arm64}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Litos.VsCodeHost/Litos.VsCodeHost.csproj"
BIN_NAME="Litos.VsCodeHost"
VERSION="${LITOS_VERSION:-1.0.0}"

PUBLISH_DIR="$REPO_ROOT/deploy/out/vscodehost/macos/$RUNTIME/publish"

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
    echo "Published (UNSIGNED): $PUBLISH_DIR/$BIN_NAME"
    echo "WARNING: unsigned — Gatekeeper will block this binary when the extension spawns it,"
    echo "with no in-app way for the user to bypass it (see this script's own header comment)."
    echo "Do not ship this build. Set APPLE_SIGN_IDENTITY (+ APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD"
    echo "to also notarize) before publishing a real release."
else
    echo "Signing with identity: $APPLE_SIGN_IDENTITY"
    codesign --force --options runtime --sign "$APPLE_SIGN_IDENTITY" "$PUBLISH_DIR/$BIN_NAME"
fi

ZIP_PATH="$REPO_ROOT/deploy/out/vscodehost/macos/$RUNTIME/Litos.VsCodeHost-$RUNTIME.zip"
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
elif [ -n "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo "Note: APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD not set, skipping notarization (signed but not notarized)."
fi

echo ""
echo "Published to: $PUBLISH_DIR/$BIN_NAME"
echo "Release archive: $ZIP_PATH"
echo "Copy into the extension bundle with:"
echo "  cp \"$PUBLISH_DIR/$BIN_NAME\" \"src/Litos.VsCode/bin/$RUNTIME/\""
