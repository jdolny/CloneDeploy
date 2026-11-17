#!/bin/bash
set -euo pipefail
umask 077
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 && "$EUID" -eq 0 && "$#" -eq 0 ]] || { echo "Run the configured installer as root, without arguments." >&2; exit 1; }
package_dir="$(cd -- "$(dirname -- "$0")" && pwd -P)"
root="/Library/Application Support/CloneDeploy/Agent"
bin="$root/bin"
plist="/Library/LaunchDaemons/com.clonedeploy.agent.plist"
for path in /Library "/Library/Application Support" "/Library/Application Support/CloneDeploy" "$root" "$bin" /Library/LaunchDaemons "$plist" /Library/Logs /Library/Logs/CloneDeploy /Library/Logs/CloneDeploy/agent.log; do
    [[ ! -L "$path" ]] || { echo "Refusing redirected installation path." >&2; exit 1; }
done
[[ "$package_dir" != "$bin" && -f "$package_dir/CloneDeploy-Agent.MacOS" && -f "$package_dir/com.clonedeploy.agent.plist" ]] || exit 1
[[ -f "$package_dir/enrollment.json" || -f "$root/configuration.json" ]] || { echo "Initial installation requires a configured package." >&2; exit 1; }
install -d -o root -g wheel -m 700 "$root"
stage=$(mktemp -d "$root/.install.XXXXXX")
stopped=0
moved=0
had_bin=0
had_plist=0
success=0
snapshot=0
finish() {
    result=$?
    trap - EXIT TERM INT
    if [[ "$success" -ne 1 && "$stopped" -eq 1 ]]; then
        if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then
            launchctl bootout system/com.clonedeploy.agent || { echo "Rollback could not stop agent; retained recovery files at $stage" >&2; exit 1; }
        fi
        if [[ "$snapshot" -ne 1 ]]; then
            if [[ -f "$plist" ]]; then launchctl bootstrap system "$plist" || true; fi
            echo "Install failed before state snapshot; retained staging at $stage" >&2
            exit "$result"
        fi
        if [[ "$moved" -eq 1 ]]; then
            mv "$bin" "$stage/failed-bin"
        fi
        if [[ "$had_bin" -eq 1 && -d "$stage/previous-bin" ]]; then mv "$stage/previous-bin" "$bin"; fi
        for name in configuration.json identity.json configuration-pending.json; do
            [[ ! -L "$root/$name" ]] || exit 1
            if [[ -f "$stage/$name" ]]; then cp -p "$stage/$name" "$root/$name"; else rm -f "$root/$name"; fi
        done
        if [[ "$had_plist" -eq 1 ]]; then
            cp -p "$stage/previous.plist" "$plist"
            launchctl bootstrap system "$plist" || { echo "Rollback files restored; daemon restart failed." >&2; exit 1; }
        else rm -f "$plist"; fi
    fi
    case "$stage" in "$root"/.install.*) rm -rf -- "$stage" ;; *) exit 1 ;; esac
    exit "$result"
}
trap finish EXIT
trap 'exit 1' TERM INT
# Start with an empty payload: an unconfigured reinstall must not reuse old enrollment.json.
mkdir "$stage/payload"
cp -R "$package_dir/." "$stage/payload/"
chown -R root:wheel "$stage/payload"
chmod -R go-rwx "$stage/payload"
chmod 700 "$stage/payload/CloneDeploy-Agent.MacOS"
plutil -lint "$stage/payload/com.clonedeploy.agent.plist"
if launchctl print system/com.clonedeploy.agent >/dev/null 2>&1; then launchctl bootout system/com.clonedeploy.agent; fi
stopped=1
# Snapshot only after stopping; never restore a partially observed identity/configuration pair.
for name in configuration.json identity.json configuration-pending.json; do
    [[ ! -L "$root/$name" ]] || exit 1
    if [[ -f "$root/$name" ]]; then cp -p "$root/$name" "$stage/$name"; fi
done
if [[ -f "$plist" ]]; then cp -p "$plist" "$stage/previous.plist"; had_plist=1; fi
snapshot=1
if [[ -d "$bin" ]]; then mv "$bin" "$stage/previous-bin"; had_bin=1; fi
mv "$stage/payload" "$bin"
moved=1
"$bin/CloneDeploy-Agent.MacOS" --import-package
install -d -o root -g wheel -m 700 /Library/Logs/CloneDeploy
touch /Library/Logs/CloneDeploy/agent.log
chown root:wheel /Library/Logs/CloneDeploy/agent.log
chmod 600 /Library/Logs/CloneDeploy/agent.log
install -o root -g wheel -m 644 "$bin/com.clonedeploy.agent.plist" "$plist"
launchctl bootstrap system "$plist"
success=1
echo "CloneDeploy agent installed. Enrollment changes may require reset approval."
