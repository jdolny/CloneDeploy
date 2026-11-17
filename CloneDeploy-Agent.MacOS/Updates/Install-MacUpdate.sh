#!/bin/bash
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$EUID" -eq 0 && "$#" -eq 3 && "$1" =~ ^[a-f0-9]{32}$ && "$2" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$3" =~ ^[A-Fa-f0-9]{64}$ ]] || exit 1
root="/Library/Application Support/CloneDeploy/Agent"
attempt="$root/Updates/$1"
package="$attempt/package.pkg"
backup="$attempt/previous-bin"
plist="/Library/LaunchDaemons/com.clonedeploy.agent.plist"
health="$root/Updates/healthy-$2"
for target in /Library "/Library/Application Support" "/Library/Application Support/CloneDeploy" "$root" "$root/bin" "$root/Updates" "$attempt" "$package" "$backup" "$health" /Library/LaunchDaemons "$plist"; do
    [[ ! -L "$target" ]] || { echo "Refusing symbolic link: $target"; exit 1; }
done
[[ -f "$package" && -d "$root/bin" && ! -e "$backup" && -f "$plist" && ! -e "$root/EnrollmentPending" ]] || exit 1
# Verify immediately before installation, including publisher continuity with this agent.
actual=$(shasum -a 256 "$package" | awk '{print toupper($1)}')
[[ "$actual" == "$(printf '%s' "$3" | tr 'a-f' 'A-F')" ]] || { echo 'Package hash mismatch.'; exit 1; }
codesign --verify --strict "$root/bin/CloneDeploy-Agent.MacOS"
team=$(codesign -dv --verbose=4 "$root/bin/CloneDeploy-Agent.MacOS" 2>&1 | sed -n 's/^TeamIdentifier=//p')
[[ "$team" =~ ^[A-Z0-9]{10}$ ]] || { echo 'Installed agent has no trusted signing team. Install the signed enrollment bundle first.'; exit 1; }
signature=$(pkgutil --check-signature "$package")
printf '%s\n' "$signature" | grep -E "1[.] Developer ID Installer: .*\\($team\\)" >/dev/null || { echo 'Package publisher differs from installed agent.'; exit 1; }
spctl --assess --type install "$package"
stopped=0
moved=0
success=0
diagnostics() {
    echo "--- launchd diagnostics ---"
    plutil -lint "$plist" 2>&1 || true
    ls -ld "$root" "$root/bin" "$root/bin/CloneDeploy-Agent.MacOS" "$plist" 2>&1 || true
    launchctl print system/com.clonedeploy.agent 2>&1 || true
    echo "--- executable signature/quarantine (unsigned development builds may fail verification) ---"
    codesign --verify --verbose=2 "$root/bin/CloneDeploy-Agent.MacOS" 2>&1 || true
    xattr -p com.apple.quarantine "$root/bin/CloneDeploy-Agent.MacOS" 2>&1 || true
    # Only matching security/service events; never dump configuration or enrollment files.
    /usr/bin/log show --last 3m --style compact --predicate '(process == "launchd" OR process == "amfid" OR process == "syspolicyd") AND eventMessage CONTAINS[c] "clonedeploy"' 2>&1 | tail -n 60 || true
}
stop_daemon() {
    local check
    if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then
        if ! launchctl bootout system/com.clonedeploy.agent; then
            # A concurrently completed removal is also acceptable.
            if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then return 1; fi
        fi
    fi
    stopped=1
    # bootout returning is not sufficient proof that the label can be reused.
    for ((check=0; check<15; check++)); do
        if ! launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then return 0; fi
        sleep 2
    done
    echo "Daemon is still registered after waiting 30 seconds; refusing to replace running files."
    return 1
}
start_daemon() {
    local retry
    for ((retry=1; retry<=10; retry++)); do
        if launchctl bootstrap system "$plist"; then return 0; fi
        echo "Daemon bootstrap attempt $retry/10 failed."
        # Some launchd failures leave a registration behind. Do not remove it or
        # swap files underneath it: the authenticated health check is definitive.
        if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then
            echo "Daemon is registered; continuing to the health check."
            return 0
        fi
        if [[ "$retry" -lt 10 ]]; then sleep 2; fi
    done
    return 1
}
finish() {
    result=$?
    trap - EXIT TERM INT
    if [[ "$success" -ne 1 && "$stopped" -eq 1 ]]; then
        echo "Update did not complete; restoring previous agent."
        if ! stop_daemon; then
            diagnostics
            printf 'Failed; could not stop daemon safely for rollback. Manual recovery required\n' > "$attempt/result.txt"
            exit 1
        fi
        if [[ "$moved" -eq 1 ]]; then
            if [[ -d "$root/bin" ]]; then mv "$root/bin" "$attempt/failed-bin"; fi
            mv "$backup" "$root/bin"
            cp -p "$attempt/previous.plist" "$plist"
        fi
        echo "Starting restored system daemon: com.clonedeploy.agent"
        if start_daemon; then
            printf 'Failed; previous agent files restored and launchd registration restored\n' > "$attempt/result.txt"
        else
            diagnostics
            printf 'Failed; previous agent files restored but launchd restart failed. Manual restart required\n' > "$attempt/result.txt"
        fi
        exit 1
    fi
    exit "$result"
}
trap finish EXIT
trap 'exit 1' TERM INT
# Never import enrollment settings from an update. Existing identity/configuration stay beside bin.
rm -f "$health"
echo "Stopping system daemon: com.clonedeploy.agent"
if ! stop_daemon; then diagnostics; exit 1; fi
cp -Rp "$root/bin" "$backup"
cp -p "$plist" "$attempt/previous.plist"
moved=1
installer -pkg "$package" -target /
installed=$("$root/bin/CloneDeploy-Agent.MacOS" --version)
[[ "$installed" == "$2" || "$installed" == "$2.0" ]] || { echo 'Installed version does not match the release.'; exit 1; }
for ((i=0; i<60; i++)); do
    if [[ -f "$health" ]]; then
        success=1
        printf 'Installed %s; authenticated check-in confirmed\n' "$2" > "$attempt/result.txt"
        echo "Mac agent $2 update completed."
        exit 0
    fi
    sleep 2
done
echo "New agent did not confirm a check-in within 120 seconds."
diagnostics
exit 1
