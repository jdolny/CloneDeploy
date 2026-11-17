#!/bin/bash
set -euo pipefail
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$EUID" -eq 0 ]] || exit 1
source_dir="/Library/Application Support/CloneDeploy/Agent/bin"
app="/Applications/CloneDeploy Portal.app"
plist="/Library/LaunchAgents/com.clonedeploy.agent.portal.plist"
for target in /Applications "$app" /Library/LaunchAgents "$plist" "$source_dir/CloneDeploy Portal.app"; do
    [[ ! -L "$target" ]] || { echo "Refusing symbolic link: $target" >&2; exit 1; }
done
version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$source_dir/CloneDeploy Portal.app/Contents/Info.plist")
installed=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist" 2>/dev/null || true)
if [[ "$version" == "$installed" && -f "$plist" ]]; then exit 0; fi
# Stage on the same filesystem. Only the public UI bundle is copied out of private agent storage.
staging=$(mktemp -d /Applications/.clonedeploy-portal.XXXXXX)
trap 'rmdir "$staging" 2>/dev/null || true' EXIT
cp -R "$source_dir/CloneDeploy Portal.app" "$staging/CloneDeploy Portal.app"
chown -R root:wheel "$staging/CloneDeploy Portal.app"
find "$staging/CloneDeploy Portal.app" -type d -exec chmod 755 {} +
find "$staging/CloneDeploy Portal.app" -type f -exec chmod 644 {} +
chmod 755 "$staging/CloneDeploy Portal.app/Contents/MacOS/CloneDeploy-Agent.MacOS.Portal"
uid=$(stat -f %u /dev/console)
if [[ "$uid" =~ ^[0-9]+$ && "$uid" -ge 501 ]]; then
    launchctl bootout "gui/$uid/com.clonedeploy.agent.portal" >/dev/null 2>&1 || true
fi
if [[ -e "$app" ]]; then mv "$app" "$staging/previous.app"; fi
if ! mv "$staging/CloneDeploy Portal.app" "$app"; then
    if [[ -d "$staging/previous.app" ]]; then mv "$staging/previous.app" "$app"; fi
    exit 1
fi
install -o root -g wheel -m 644 "$source_dir/com.clonedeploy.agent.portal.plist" "$plist"
if [[ "$uid" =~ ^[0-9]+$ && "$uid" -ge 501 ]]; then
    # A logged-in user's GUI domain may be unavailable during logout; next login starts it.
    echo "Starting portal LaunchAgent: gui/$uid/com.clonedeploy.agent.portal"
    if ! launchctl bootstrap "gui/$uid" "$plist"; then
        echo "Portal LaunchAgent startup failed; device daemon can continue. Try opening the portal from Applications or log out/in." >&2
    fi
fi
# Remove only our generated staging directory, which can contain the previous public UI.
case "$staging" in /Applications/.clonedeploy-portal.*) rm -rf -- "$staging" ;; *) exit 1 ;; esac
