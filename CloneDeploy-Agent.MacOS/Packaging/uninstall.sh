#!/bin/bash
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$(uname -s)" == Darwin && "$EUID" -eq 0 ]] || { echo "Run as root on macOS." >&2; exit 1; }
remove_data=0
if [[ "$#" -eq 1 && "$1" == --remove-data ]]; then remove_data=1
elif [[ "$#" -ne 0 ]]; then echo "Usage: uninstall.sh [--remove-data]" >&2; exit 1; fi
root="/Library/Application Support/CloneDeploy/Agent"
app="/Applications/CloneDeploy Portal.app"
daemon="/Library/LaunchDaemons/com.clonedeploy.agent.plist"
portal="/Library/LaunchAgents/com.clonedeploy.agent.portal.plist"
# Check all ancestors; never accept a caller-supplied cleanup location.
check_path() {
    local current="$1"
    while [[ "$current" != / && -n "$current" ]]; do
        [[ ! -L "$current" ]] || { echo "Refusing redirected uninstall path." >&2; exit 1; }
        current="$(dirname "$current")"
    done
}
remove_tree() {
    case "$1" in "$root"|"$root/bin"|"$app"|/Library/Logs/CloneDeploy|/private/var/run/clonedeploy-portal) ;; *) exit 1 ;; esac
    check_path "$1"
    # rm does not traverse symlinks inside the tree; the fixed root and ancestors must not be links.
    rm -rf -- "$1"
}
main() {
    for path in "$root" "$root/bin" "$app" "$daemon" "$portal" /Library/Logs/CloneDeploy /private/var/run/clonedeploy-portal; do check_path "$path"; done
    if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then launchctl bootout system/com.clonedeploy.agent; fi
    if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then echo "Agent is still registered; refusing cleanup." >&2; exit 1; fi
    # Unload every registered local user's portal without opening UI or Keychain prompts.
    while read -r user uid; do
        [[ "$uid" =~ ^[0-9]+$ && "$uid" -ge 501 ]] || continue
        if launchctl print "gui/$uid/com.clonedeploy.agent.portal" >/dev/null 2>&1; then
            launchctl bootout "gui/$uid/com.clonedeploy.agent.portal"
        fi
    done < <(dscl . -list /Users UniqueID)
    if [[ -f "$root/bin/CloneDeploy-Agent.MacOS" ]]; then "$root/bin/CloneDeploy-Agent.MacOS" --check-state-lock; fi
    rm -f -- "$daemon" "$portal"
    remove_tree "$app"
    remove_tree /private/var/run/clonedeploy-portal
    if [[ "$remove_data" -eq 1 ]]; then
        remove_tree "$root"
        remove_tree /Library/Logs/CloneDeploy
    else remove_tree "$root/bin"; fi
    if pkgutil --pkg-info com.clonedeploy.agent >/dev/null 2>&1; then pkgutil --forget com.clonedeploy.agent >/dev/null; fi
    echo "CloneDeploy agent uninstalled. Server records are unchanged."
    if [[ "$remove_data" -eq 0 ]]; then
        echo "Configuration, identity, databases and logs were preserved. For a clean uninstall, run this script with --remove-data before removing the installed binaries."
    fi
}
main
