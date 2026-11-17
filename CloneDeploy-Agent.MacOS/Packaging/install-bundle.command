#!/bin/bash
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$EUID" -eq 0 && "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
    echo 'Extract the bundle, then run: sudo /bin/bash /path/to/install.command' >&2; exit 1;
}
bundle="$(cd -- "$(dirname -- "$0")" && pwd -P)"
root="/Library/Application Support/CloneDeploy/Agent"
pending="$root/EnrollmentPending"
for path in /Library "/Library/Application Support" "/Library/Application Support/CloneDeploy" "$root" "$pending"; do
    [[ ! -L "$path" ]] || { echo 'Refusing redirected enrollment path.' >&2; exit 1; }
done
[[ -f "$bundle/enrollment.json" && -f "$bundle/server-ca.cer" ]] || { echo 'Enrollment files are missing.' >&2; exit 1; }
# Use Apple's signature and Gatekeeper checks; never modify the signed package.
pkgutil --check-signature "$bundle/CloneDeploy-Agent-macOS-arm64.pkg"
spctl --assess --type install "$bundle/CloneDeploy-Agent-macOS-arm64.pkg"
install -d -o root -g wheel -m 700 "$root"
mkdir "$pending" # Refuse concurrent installation or an unconsumed previous request.
chown root:wheel "$pending"
chmod 700 "$pending"
cleanup() { rm -f "$pending/enrollment.json" "$pending/server-ca.cer"; rmdir "$pending"; }
trap cleanup EXIT
install -o root -g wheel -m 600 "$bundle/enrollment.json" "$pending/enrollment.json"
install -o root -g wheel -m 600 "$bundle/server-ca.cer" "$pending/server-ca.cer"
installer -pkg "$bundle/CloneDeploy-Agent-macOS-arm64.pkg" -target /
