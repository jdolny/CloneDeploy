#!/bin/bash
set -euo pipefail
: "${RUNNER_TEMP:?}" "${MAC_APPLICATION_P12_BASE64:?}" "${MAC_INSTALLER_P12_BASE64:?}"
: "${MAC_APPLICATION_P12_PASSWORD:?}" "${MAC_INSTALLER_P12_PASSWORD:?}"
: "${APPLE_ID:?}" "${APPLE_TEAM_ID:?}" "${APPLE_APP_SPECIFIC_PASSWORD:?}"
[[ "$(uname -m)" == arm64 ]] || { echo 'An ARM64 Mac runner is required.' >&2; exit 1; }
umask 077
work="$RUNNER_TEMP/clonedeploy-mac-sign"
mkdir "$work"
keychain="$work/signing.keychain-db"
password=$(openssl rand -hex 32)
echo "::add-mask::$password"
cleanup() {
    security delete-keychain "$keychain" >/dev/null 2>&1 || true
    rm -f "$work/application.p12" "$work/installer.p12"
}
trap cleanup EXIT
printf '%s' "$MAC_APPLICATION_P12_BASE64" | base64 --decode > "$work/application.p12"
printf '%s' "$MAC_INSTALLER_P12_BASE64" | base64 --decode > "$work/installer.p12"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$work/application.p12" -k "$keychain" -P "$MAC_APPLICATION_P12_PASSWORD" -T /usr/bin/codesign
security import "$work/installer.p12" -k "$keychain" -P "$MAC_INSTALLER_P12_PASSWORD" -T /usr/bin/productsign -T /usr/bin/pkgbuild
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
security list-keychains -d user -s "$keychain" "$HOME/Library/Keychains/login.keychain-db"
identities=$(security find-identity -v "$keychain")
application=$(printf '%s\n' "$identities" | sed -n 's/.*"\(Developer ID Application:.*\)".*/\1/p')
installer=$(printf '%s\n' "$identities" | sed -n 's/.*"\(Developer ID Installer:.*\)".*/\1/p')
[[ "$application" == *"($APPLE_TEAM_ID)" && "$installer" == *"($APPLE_TEAM_ID)" && "$application" != *$'\n'* && "$installer" != *$'\n'* ]] || { echo 'Expected one Developer ID Application and Installer identity matching APPLE_TEAM_ID.' >&2; exit 1; }
xcrun notarytool store-credentials clonedeploy-ci --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD" --keychain "$keychain"
# Make the temporary keychain the default for the signing script's notary profile lookup.
security default-keychain -d user -s "$keychain"
cp artifacts/mac-agent/CloneDeploy-Agent-macOS-arm64.pkg "$work/unsigned.pkg"
bash Build/Sign-MacRelease.sh "$work" "$application" "$installer" clonedeploy-ci
cp "$work/signed.pkg" artifacts/mac-agent/CloneDeploy-Agent-macOS-arm64.pkg
cp "$work/agent.tar.gz" artifacts/mac-agent/CloneDeploy-Agent-macOS-arm64.tar.gz
cp artifacts/mac-agent/osx-arm64/release.json artifacts/mac-agent/release.json
