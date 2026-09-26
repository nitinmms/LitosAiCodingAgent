#!/usr/bin/env bash
# Publishes Litos.VsCodeHost as a self-contained macOS executable — a plain headless binary, not
# an .app bundle (unlike publish-macos.sh/Litos.Gui): this process has no window, no Dock icon, and
# no Info.plist — it's a background agent host the Litos VS Code extension (src/Litos.VsCode)
# spawns silently as a child process and bundles per-RID under src/Litos.VsCode/bin/<rid>/ (see
# ReadMe_VsCodeExtension.md §6). Structurally identical to the now-shelved Litos.Console's own
# macOS publish script (deploy/publish-console-macos.sh) — that script's shape is still the correct
# reference here even though Litos.Console itself is shelved, since Litos.VsCodeHost is the same
# kind of plain headless binary.
#
# Entitlements ARE required, though, despite the "no bundle" framing above — an earlier version of
# this script assumed entitlements were an .app-bundle-only concern and omitted them, which shipped
# a real signed+notarized-but-broken build: confirmed live, running the signed osx-arm64 binary
# printed "Failed to create CoreCLR, HRESULT: 0x80070008" and got SIGKILL'd (exit 137) when the VS
# Code extension spawned it. Root cause: hardened runtime (--options runtime, required for
# notarization) blocks CoreCLR's JIT/dynamic-library-loading unless the process carries the same
# com.apple.security.cs.* entitlements publish-macos.sh already applies to Litos.Gui's .app bundle
# — deploy/entitlements.plist is not bundle-specific, it applies to any hardened-runtime-signed
# Mach-O binary. Disabling EnableCompressionInSingleFile (Litos.VsCodeHost.csproj) was a necessary
# but insufficient fix on its own; entitlements were the actual missing piece.
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
VERSION="${LITOS_VERSION:-1.0.0}"

PUBLISH_DIR="$REPO_ROOT/deploy/out/vscodehost/macos/$RUNTIME/publish"

# TWO binaries ship together, both self-contained single-file, both into the SAME publish dir:
#
#   Litos.VsCodeHost   the agent host the extension spawns
#   Litos.Kernel.Host  the Programmatic Tool Calling (/ptc) kernel, which Litos.VsCodeHost spawns
#                      in turn to run model-written code out-of-process
#
# Same directory is load-bearing, not tidiness: KernelHostLocator finds the kernel by looking
# beside the launched executable (see its CandidateSiblingDirectories). They are published in one
# pass so neither can be signed, notarized and shipped without the other.
#
# Both need the SAME signing and entitlements treatment. This script originally handled only the
# host, and a kernel binary left unsigned here would reproduce the exact failure documented in this
# file's header — a signed, notarized, broken build — except later and more confusingly: the kernel
# is spawned lazily, only once a user enables /ptc and the model writes its first script, so
# Gatekeeper would kill it far from any obvious cause and PTC would simply look broken.
BIN_NAMES=("Litos.VsCodeHost" "Litos.Kernel.Host")
PROJECTS=(
    "$REPO_ROOT/src/Litos.VsCodeHost/Litos.VsCodeHost.csproj"
    "$REPO_ROOT/src/Litos.Kernel.Host/Litos.Kernel.Host.csproj"
)

for i in "${!PROJECTS[@]}"; do
    echo "Publishing ${BIN_NAMES[$i]} ($RUNTIME)..."
    dotnet publish "${PROJECTS[$i]}" \
        -c Release \
        -r "$RUNTIME" \
        -o "$PUBLISH_DIR" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:Version="$VERSION" \
        -p:InformationalVersion="$VERSION"
    chmod +x "$PUBLISH_DIR/${BIN_NAMES[$i]}"
done

# CI sets LITOS_REQUIRE_NOTARIZATION=1 so a missing or empty secret fails the release instead of
# falling through to the unsigned/un-notarized branches below, which only warn — and a warning in a
# CI log is how an unsigned kernel would otherwise reach the Marketplace.
if [ "${LITOS_REQUIRE_NOTARIZATION:-0}" = "1" ]; then
    for var in APPLE_SIGN_IDENTITY APPLE_ID APPLE_TEAM_ID APPLE_APP_PASSWORD; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: LITOS_REQUIRE_NOTARIZATION=1 but $var is not set." >&2
            exit 1
        fi
    done
fi

if [ -z "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo ""
    for BIN_NAME in "${BIN_NAMES[@]}"; do
        echo "Published (UNSIGNED): $PUBLISH_DIR/$BIN_NAME"
    done
    echo "WARNING: unsigned — Gatekeeper will block these binaries when the extension spawns them,"
    echo "with no in-app way for the user to bypass it (see this script's own header comment)."
    echo "Do not ship this build. Set APPLE_SIGN_IDENTITY (+ APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD"
    echo "to also notarize) before publishing a real release."
else
    echo "Signing with identity: $APPLE_SIGN_IDENTITY"
    for BIN_NAME in "${BIN_NAMES[@]}"; do
        codesign --force --options runtime \
            --entitlements "$REPO_ROOT/deploy/entitlements.plist" \
            --sign "$APPLE_SIGN_IDENTITY" "$PUBLISH_DIR/$BIN_NAME"
    done
fi

# One archive carrying both binaries: notarytool accepts a zip of several executables and issues a
# ticket covering each, so this is one submission (and one multi-minute wait) instead of two.
#
# No --keepParent here, unlike the single-binary scripts beside this one: --keepParent would nest
# everything under a "publish/" folder inside the zip. Archiving the directory's CONTENTS puts both
# executables at the archive root, which is what the release asset and the install path expect.
# Notarization itself is indifferent — it walks the archive for Mach-O binaries either way.
ZIP_PATH="$REPO_ROOT/deploy/out/vscodehost/macos/$RUNTIME/Litos.VsCodeHost-$RUNTIME.zip"
rezip() {
    rm -f "$ZIP_PATH"
    ditto -c -k "$PUBLISH_DIR" "$ZIP_PATH"
}
rezip

if [ -n "${APPLE_SIGN_IDENTITY:-}" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
    echo "Submitting for notarization..."
    # notarytool's exit code is not a reliable verdict: a submission Apple rejects ("Invalid") can
    # still exit 0 after --wait. Read the final status from its JSON and fail on anything but
    # Accepted, printing Apple's log so the rejected binary (host or kernel) is named in CI output.
    NOTARY_JSON="$(xcrun notarytool submit "$ZIP_PATH" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait --output-format json)"
    echo "$NOTARY_JSON"
    NOTARY_STATUS="$(printf '%s' "$NOTARY_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))')"
    if [ "$NOTARY_STATUS" != "Accepted" ]; then
        NOTARY_ID="$(printf '%s' "$NOTARY_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))')"
        echo "ERROR: notarization status is '$NOTARY_STATUS', not 'Accepted'." >&2
        if [ -n "$NOTARY_ID" ]; then
            xcrun notarytool log "$NOTARY_ID" \
                --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" || true
        fi
        exit 1
    fi

    # Stapling requires the original signed binaries, not the zip — staple each, then re-zip.
    for BIN_NAME in "${BIN_NAMES[@]}"; do
        xcrun stapler staple "$PUBLISH_DIR/$BIN_NAME" || echo "Note: stapling a plain executable (not a bundle) is not always supported; notarization ticket for $BIN_NAME is still valid online."
    done
    rezip
elif [ -n "${APPLE_SIGN_IDENTITY:-}" ]; then
    echo "Note: APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD not set, skipping notarization (signed but not notarized)."
fi

echo ""
for BIN_NAME in "${BIN_NAMES[@]}"; do
    echo "Published to: $PUBLISH_DIR/$BIN_NAME"
done
echo "Release archive: $ZIP_PATH"
echo "Copy into the extension bundle with:"
echo "  cp \"$PUBLISH_DIR\"/{Litos.VsCodeHost,Litos.Kernel.Host} \"src/Litos.VsCode/bin/$RUNTIME/\""
