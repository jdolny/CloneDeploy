import '../vendor/xterm/xterm.js';
import '../vendor/xterm/addon-fit.js';
export { ensureSession } from './browserSession.js';

const sessions = new WeakMap();
const stylesheet = document.createElement('link');
stylesheet.rel = 'stylesheet';
stylesheet.href = new URL('../vendor/xterm/xterm.css', import.meta.url).href;
document.head.appendChild(stylesheet);
const styled = new Promise(resolve => {
    if (stylesheet.sheet) resolve();
    else { stylesheet.onload = resolve; stylesheet.onerror = resolve; }
});

export async function connect(host, id, reference, identity = 'NT AUTHORITY\\SYSTEM') {
    disconnect(host);
    await styled;
    if (!host.isConnected) throw new Error(window.cloneDeployText("Js.remoteTerminal.TerminalPanelClosed"));
    const terminal = new globalThis.Terminal({
        cursorBlink: true, fontSize: 14, scrollback: 3000, disableStdin: true,
        theme: { background: '#111827', foreground: '#e5e7eb' }
    });
    const fit = new globalThis.FitAddon.FitAddon();
    terminal.loadAddon(fit);
    terminal.open(host);
    const socket = new WebSocket(new URL(`/api/ui/remote-terminal/${id}/viewer`, location.href).href.replace(/^http/, 'ws'));
    socket.binaryType = 'arraybuffer';
    let closed = false;
    let pendingBytes = 0;
    let resizeFrame = 0;
    const notify = (connected, message) => reference.invokeMethodAsync('ConnectionChanged', id, connected, message).catch(() => {});
    const send = message => {
        if (closed || socket.readyState !== WebSocket.OPEN) return;
        if (socket.bufferedAmount > 128 * 1024) { finish(window.cloneDeployText("Js.remoteTerminal.TerminalDisconnectedBecauseInputWasArrivingTooQuickly")); return; }
        socket.send(JSON.stringify(message));
    };
    const resize = () => {
        if (closed || host.clientWidth < 50 || host.clientHeight < 50) return;
        fit.fit();
        const columns = Math.max(20, Math.min(400, terminal.cols));
        const rows = Math.max(5, Math.min(200, terminal.rows));
        if (terminal.cols !== columns || terminal.rows !== rows) terminal.resize(columns, rows);
        send({type: 'resize', columns, rows});
    };
    const input = terminal.onData(data => {
        if (data.length > 32768) { notify(true, window.cloneDeployText("Js.remoteTerminal.PasteIsLimitedTo32768Characters")); return; }
        // Small frames keep JSON-escaped control characters within the server limit.
        for (let offset = 0; offset < data.length;) {
            let end = Math.min(data.length, offset + 1024);
            if (end < data.length && /[\uD800-\uDBFF]/.test(data[end - 1])) end--;
            send({type: 'input', data: data.slice(offset, end)});
            offset = end;
        }
    });
    const observer = new ResizeObserver(() => {
        cancelAnimationFrame(resizeFrame);
        resizeFrame = requestAnimationFrame(resize);
    });
    observer.observe(host);
    const removed = new MutationObserver(() => { if (!host.isConnected) finish(); });
    removed.observe(document.body, {childList: true, subtree: true});
    const pagehide = () => finish();
    window.addEventListener('pagehide', pagehide);
    terminal.attachCustomKeyEventHandler(event => {
        if (event.type === 'keydown' && event.ctrlKey && event.shiftKey && event.code === 'KeyC') {
            const selected = terminal.getSelection();
            if (selected) navigator.clipboard?.writeText(selected).catch(() => {});
            return false;
        }
        return true;
    });
    function finish(message) {
        if (closed) return;
        closed = true;
        cancelAnimationFrame(resizeFrame);
        observer.disconnect(); removed.disconnect(); input.dispose();
        window.removeEventListener('pagehide', pagehide);
        socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
        socket.close();
        terminal.dispose();
        host.replaceChildren();
        sessions.delete(host);
        if (message) notify(false, message);
    }
    sessions.set(host, finish);
    socket.onopen = () => {
        terminal.options.disableStdin = false;
        resize(); terminal.focus();
        notify(true, window.cloneDeployText("Js.remoteTerminal.ConnectedAs0", identity));
    };
    socket.onmessage = event => {
        if (!(event.data instanceof ArrayBuffer)) { finish(window.cloneDeployText("Js.remoteTerminal.InvalidTerminalResponse")); return; }
        const data = new Uint8Array(event.data);
        pendingBytes += data.length;
        if (pendingBytes > 128 * 1024) { finish(window.cloneDeployText("Js.remoteTerminal.TerminalOutputExceededItsBufferLimit")); return; }
        terminal.write(data, () => {
            pendingBytes -= data.length;
            send({type: 'ack'});
        });
    };
    socket.onerror = () => finish(window.cloneDeployText("Js.remoteTerminal.CouldNotConnectToTheTerminalStreamCheckThe"));
    socket.onclose = () => finish(window.cloneDeployText("Js.remoteTerminal.TerminalEndedShellExitedConnectionClosedOrSessionTimed"));
}

export function disconnect(host) { sessions.get(host)?.(); }
