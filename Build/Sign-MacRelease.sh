#!/bin/bash
set -euo pipefail
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$(uname -s)" == Darwin && "$#" -eq 4 ]] || exit 1
work="$1"; application="$2"; installer_identity="$3"; notary_profile="$4"
cd "$work"
pkgutil --expand unsigned.pkg expanded
mkdir payload
tar -xzf expanded/Scripts/agent.tar.gz -C payload
cat > entitlements.plist <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>com.apple.security.cs.allow-jit</key><true/>
</dict></plist>
EOF
# Sign native dependencies first, then the portal bundle. Never use --deep to sign.
portal='payload/CloneDeploy Portal.app'
portal_main="$portal/Contents/MacOS/CloneDeploy-Agent.MacOS.Portal"
if [[ -n "$(find "$portal/Contents/MacOS" -name '*.dll' -print -quit)" ]]; then
    echo 'Portal contains loose managed DLLs. Rebuild with single-file portal publishing before signing.' >&2
    exit 1
fi
while IFS= read -r -d '' item; do
    # Signing the main executable also signs its containing bundle. Defer it until
    # every native dependency has been signed, regardless of find traversal order.
    [[ "$item" == "$portal_main" ]] && continue
    if file -b "$item" | grep -q 'Mach-O'; then
        chmod u+x "$item"
        codesign --force --timestamp --options runtime --entitlements entitlements.plist --sign "$application" "$item"
    fi
done < <(find payload -type f -print0)
codesign --force --timestamp --options runtime --entitlements entitlements.plist --sign "$application" 'payload/CloneDeploy Portal.app'
codesign --verify --deep --strict 'payload/CloneDeploy Portal.app'
codesign --verify --strict payload/CloneDeploy-Agent.MacOS
# This script-based installer embeds a compressed archive. Notarize its contents
# independently so the daemon and portal receive tickets as well as the installer.
ditto -c -k --keepParent payload payload.zip
xcrun notarytool submit payload.zip --keychain-profile "$notary_profile" --wait --output-format json > payload-notarization.json
/usr/bin/plutil -extract status raw -o - payload-notarization.json | grep -qx Accepted
xcrun stapler staple 'payload/CloneDeploy Portal.app'
version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' 'payload/CloneDeploy Portal.app/Contents/Info.plist')
actual=$(payload/CloneDeploy-Agent.MacOS --version)
[[ "$actual" == "$version" || "$actual" == "$version.0" ]] || { echo 'Signed agent failed its version check.' >&2; exit 1; }
COPYFILE_DISABLE=1 tar -czf agent.tar.gz -C payload .
cp agent.tar.gz expanded/Scripts/agent.tar.gz
chmod 700 expanded/Scripts/postinstall
pkgbuild --nopayload --scripts expanded/Scripts --identifier com.clonedeploy.agent --version "$version" --sign "$installer_identity" signed.pkg
xcrun notarytool submit signed.pkg --keychain-profile "$notary_profile" --wait --output-format json > notarization.json
/usr/bin/plutil -extract status raw -o - notarization.json | grep -qx Accepted
xcrun stapler staple signed.pkg
xcrun stapler validate signed.pkg
team=$(codesign -dv --verbose=4 payload/CloneDeploy-Agent.MacOS 2>&1 | sed -n 's/^TeamIdentifier=//p')
[[ "$team" =~ ^[A-Z0-9]{10}$ ]] || exit 1
pkgutil --check-signature signed.pkg | grep -E "1[.] Developer ID Installer: .*\\($team\\)"
spctl --assess --type install signed.pkg
