export { ensureSession } from './browserSession.js';
const sessions = new WeakMap();

export function connect(canvas, id, component) {
    disconnect(canvas);
    const url = new URL(`api/ui/remote-desktop/${id}/viewer`, document.baseURI);
    url.protocol = "wss:";
    const socket = new WebSocket(url);
    const state = { socket, control: false, listeners: [], drawing: false, pending: null, closed: false, lastMove: 0, requests: new Map(), monitor: null };
    sessions.set(canvas, state);
    const notify = (connected, message) => component.invokeMethodAsync("ConnectionChanged", connected, message).catch(() => disconnect(canvas));
    function send(data) {
        if (socket.readyState !== WebSocket.OPEN || socket.bufferedAmount >= 128 * 1024) return false;
        const json = JSON.stringify(data);
        if (new TextEncoder().encode(json).length > 128 * 1024) return false;
        socket.send(json); return true;
    }
    const container = canvas.closest('.remote-desktop-viewer');
    const commandStatus = container?.querySelector('[data-command-status]');
    const clipboardText = container?.querySelector('[data-clipboard-text]');
    const clipboardPanel = container?.querySelector('[data-clipboard-panel]');
    const qualitySelect = container?.querySelector('[data-quality]');
    if (qualitySelect) qualitySelect.value = 'balanced';
    const frameRateSelect = container?.querySelector('[data-frame-rate]');
    if (frameRateSelect) frameRateSelect.value = '8';
    let frameRate = '8', changingFrameRate = false;
    let quality = 'balanced', changingQuality = false;
    const monitorSelect = container?.querySelector('[data-monitor]');
    const status = text => { if (commandStatus) commandStatus.textContent = text; };
    status('');
    function request(type, values = {}) {
        return new Promise((resolve, reject) => {
            if (state.requests.size >= 8) { reject(new Error(window.cloneDeployText("Js.remoteDesktop.WaitForTheCurrentCommandToFinish"))); return; }
            const requestId = crypto.randomUUID();
            const timeout = setTimeout(() => { state.requests.delete(requestId); reject(new Error(window.cloneDeployText("Js.remoteDesktop.TheAgentDidNotRespondToTheCommand"))); }, 15000);
            state.requests.set(requestId, { resolve, reject, timeout });
            if (!send({ type, requestId, ...values })) {
                clearTimeout(timeout); state.requests.delete(requestId); reject(new Error(window.cloneDeployText("Js.remoteDesktop.TheRemoteConnectionIsBusyOrUnavailable")));
            }
        });
    }
    state.updateButtons = () => {
        if (!container) return;
        if (frameRateSelect) frameRateSelect.disabled = !state.hasFrame || changingFrameRate;
        if (qualitySelect) qualitySelect.disabled = !state.hasFrame || changingQuality;
        if (monitorSelect) monitorSelect.disabled = !state.hasFrame;
    };
    function coordinates(event) {
        const rect = canvas.getBoundingClientRect();
        // The canvas is fitted inside its element; exclude the letterboxed margins.
        const scale = Math.min(rect.width / canvas.width, rect.height / canvas.height);
        const width = canvas.width * scale, height = canvas.height * scale;
        return { monitor: state.monitor, x: Math.max(0, Math.min(1, (event.clientX - rect.left - (rect.width - width) / 2) / width)),
                 y: Math.max(0, Math.min(1, (event.clientY - rect.top - (rect.height - height) / 2) / height)) };
    }
    function listen(target, name, handler, options) {
        if (!target) return;
        target.addEventListener(name, handler, options);
        state.listeners.push(() => target.removeEventListener(name, handler, options));
    }
    const button = (selector, action) => listen(container?.querySelector(selector), 'click', async () => {
        try {
            if (!state.hasFrame) throw new Error(window.cloneDeployText("Js.remoteDesktop.WaitForTheRemoteDesktopToConnectAndDisplay"));
            if (['[data-sas]', '[data-send-clipboard]', '[data-send-text]'].includes(selector) && !state.control)
                throw new Error(window.cloneDeployText("Js.remoteDesktop.EnableMouseAndKeyboardControlBeforeSendingCommandsTo"));
            await action();
        } catch (error) {
            await component.invokeMethodAsync('ShowActionError', error.message || window.cloneDeployText("Js.remoteDesktop.TheCommandFailed")).catch(() => {});
        }
    });
    button('[data-sas]', async () => { if (state.control) status((await request('sas')).detail); });
    const sendText = async text => {
        if (!state.control) throw new Error(window.cloneDeployText("Js.remoteDesktop.EnableMouseAndKeyboardControlToSendClipboardText"));
        if (text.length > 16384 || text.includes('\0')) throw new Error(window.cloneDeployText("Js.remoteDesktop.ClipboardTextIsLimitedTo16384CharactersAnd"));
        status((await request('clipboardSet', { text })).detail);
    };
    button('[data-send-clipboard]', async () => {
        let text;
        try { text = await navigator.clipboard.readText(); }
        catch { clipboardPanel.open = true; clipboardText.focus(); throw new Error(window.cloneDeployText("Js.remoteDesktop.BrowserClipboardAccessWasDeniedPasteIntoTextClipboard")); }
        await sendText(text);
    });
    button('[data-send-text]', async () => await sendText(clipboardText.value));
    button('[data-get-clipboard]', async () => {
        const reply = await request('clipboardGet');
        if (typeof reply.text !== 'string' || reply.text.length > 16384 || reply.text.includes('\0')) throw new Error(window.cloneDeployText("Js.remoteDesktop.InvalidRemoteClipboardText"));
        clipboardText.value = reply.text || '';
        try { await navigator.clipboard.writeText(clipboardText.value); status(window.cloneDeployText("Js.remoteDesktop.RemoteTextCopiedToYourClipboard")); }
        catch { clipboardPanel.open = true; status(window.cloneDeployText("Js.remoteDesktop.RemoteTextReceivedChooseCopyTextLocallyOrSelect")); }
    });
    button('[data-copy-text]', async () => { await navigator.clipboard.writeText(clipboardText.value); status(window.cloneDeployText("Js.remoteDesktop.TextCopiedToYourClipboard")); });
    button('[data-fullscreen]', async () => {
        if (document.fullscreenElement === canvas) await document.exitFullscreen();
        else await canvas.requestFullscreen();
    });
    listen(document, 'fullscreenchange', () => {
        send({ type: 'release' });
        if (document.fullscreenElement === canvas) canvas.focus();
        const button = container?.querySelector('[data-fullscreen]');
        if (button) button.textContent = document.fullscreenElement === canvas ? window.cloneDeployText("Js.remoteDesktop.ExitFullscreen") : window.cloneDeployText("Js.remoteDesktop.Fullscreen");
    });
    listen(frameRateSelect, 'change', async () => {
        if (!state.hasFrame || changingFrameRate) return;
        const fps = frameRateSelect.value;
        changingFrameRate = true; state.updateButtons();
        try { status((await request('frameRate', { fps })).detail); frameRate = fps; }
        catch (error) { status(error.message); }
        finally {
            changingFrameRate = false;
            if (!state.closed) { frameRateSelect.value = frameRate; state.updateButtons(); }
        }
    });
    listen(qualitySelect, 'change', async () => {
        if (!state.hasFrame || changingQuality) return;
        const preset = qualitySelect.value;
        changingQuality = true; state.updateButtons();
        try { status((await request('quality', { preset })).detail); quality = preset; }
        catch (error) { status(error.message); }
        finally {
            changingQuality = false;
            if (!state.closed) { qualitySelect.value = quality; state.updateButtons(); }
        }
    });
    listen(monitorSelect, 'change', async () => {
        state.switchingMonitor = true; send({ type: 'release' });
        try { status((await request('monitor', { monitor: monitorSelect.value })).detail); }
        catch (error) { status(error.message); }
        finally { state.switchingMonitor = false; }
    });
    listen(canvas, "pointermove", e => {
        if (!state.control || state.switchingMonitor || performance.now() - state.lastMove < 40) return;
        state.lastMove = performance.now(); send({ type: "move", ...coordinates(e) });
    });
    for (const name of ["pointerdown", "pointerup"]) listen(canvas, name, e => {
        if (!state.control || state.switchingMonitor) return;
        e.preventDefault(); canvas.focus();
        if (name === "pointerdown") canvas.setPointerCapture(e.pointerId);
        send({ type: "button", button: e.button, down: name === "pointerdown", ...coordinates(e) });
    });
    listen(canvas, "wheel", e => {
        if (!state.control || state.switchingMonitor) return;
        e.preventDefault(); send({ type: "wheel", delta: e.deltaY > 0 ? -120 : 120, ...coordinates(e) });
    }, { passive: false });
    listen(canvas, "contextmenu", e => { if (state.control) e.preventDefault(); });
    for (const name of ["keydown", "keyup"]) listen(canvas, name, e => {
        if (!state.control) return;
        e.preventDefault(); e.stopPropagation();
        send({ type: "key", key: e.keyCode, code: e.code, keyText: e.key, down: name === "keydown" });
    });
    listen(canvas, "blur", () => send({ type: "release" }));
    listen(window, "blur", () => send({ type: "release" }));
    listen(canvas, "pointercancel", () => send({ type: "release" }));
    state.observer = new MutationObserver(() => { if (!canvas.isConnected) disconnect(canvas); });
    state.observer.observe(document.body, { childList: true, subtree: true });
    state.timeout = setTimeout(() => {
        if (!state.hasFrame) { disconnect(canvas); notify(false, window.cloneDeployText("Js.remoteDesktop.NoDesktopFramesReceivedCheckTheAgentLogAnd")); }
    }, 25000);
    socket.onmessage = async e => {
        if (typeof e.data === 'string') {
            if (e.data.length > 128 * 1024) return;
            try {
                const reply = JSON.parse(e.data);
                if (reply.type === 'monitors' && Array.isArray(reply.monitors) && monitorSelect) {
                    monitorSelect.replaceChildren();
                    for (const monitor of reply.monitors) {
                        const option = document.createElement('option'); option.value = monitor.id;
                        option.textContent = `${monitor.id} · ${monitor.width} × ${monitor.height}${monitor.primary ? ' · Primary' : ''}`;
                        monitorSelect.appendChild(option);
                    }
                    monitorSelect.value = reply.selected; state.monitor = reply.selected;
                    state.updateButtons();
                } else if (state.requests.has(reply.requestId)) {
                    const waiting = state.requests.get(reply.requestId); state.requests.delete(reply.requestId); clearTimeout(waiting.timeout);
                    if (reply.success) waiting.resolve(reply); else waiting.reject(new Error(reply.detail || window.cloneDeployText("Js.remoteDesktop.TheDesktopCommandFailed")));
                }
            } catch { status(window.cloneDeployText("Js.remoteDesktop.InvalidDesktopControlResponse")); }
            return;
        }
        if (!(e.data instanceof Blob) || e.data.size > 4 * 1024 * 1024) return;
        state.pending = e.data;
        if (state.drawing) return;
        state.drawing = true;
        try {
            while (state.pending && !state.closed) {
                const frame = state.pending; state.pending = null;
                const bitmap = await createImageBitmap(frame);
                if (!state.closed) {
                    if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
                        canvas.width = bitmap.width; canvas.height = bitmap.height;
                    }
                    canvas.getContext("2d").drawImage(bitmap, 0, 0);
                    if (!state.hasFrame) { state.hasFrame = true; clearTimeout(state.timeout); state.updateButtons(); notify(true, window.cloneDeployText("Js.remoteDesktop.ConnectedViewingOnlyUntilControlIsEnabled")); }
                }
                bitmap.close();
            }
        } catch { disconnect(canvas); notify(false, window.cloneDeployText("Js.remoteDesktop.CouldNotDecodeTheRemoteDesktopStream")); }
        finally { state.drawing = false; }
    };
    socket.onclose = () => {
        if (!state.closed) { disconnect(canvas); notify(false, window.cloneDeployText("Js.remoteDesktop.RemoteViewingEndedCheckTheAgentLogForConnection")); }
    };
}

export function setControl(canvas, enabled) {
    const state = sessions.get(canvas);
    if (!state) return;
    if (!enabled && state.socket.readyState === WebSocket.OPEN) state.socket.send(JSON.stringify({ type: "release" }));
    state.control = enabled;
    state.updateButtons();
    canvas.style.outlineColor = enabled ? "#e69c26" : "transparent";
    if (enabled) canvas.focus();
}

export function disconnect(canvas) {
    const state = sessions.get(canvas);
    if (!state) return;
    state.closed = true; state.pending = null;
    for (const waiting of state.requests.values()) { clearTimeout(waiting.timeout); waiting.reject(new Error(window.cloneDeployText("Js.remoteDesktop.RemoteSessionDisconnected"))); }
    state.requests.clear();
    const container = canvas.closest('.remote-desktop-viewer');
    if (container) {
        container.querySelector('[data-clipboard-text]').value = '';
        container.querySelector('[data-clipboard-panel]').open = false;
        const frameRateSelect = container.querySelector('[data-frame-rate]');
        if (frameRateSelect) { frameRateSelect.disabled = true; frameRateSelect.value = '8'; }
        const qualitySelect = container.querySelector('[data-quality]');
        if (qualitySelect) { qualitySelect.disabled = true; qualitySelect.value = 'balanced'; }
        container.querySelector('[data-monitor]').disabled = true;
        container.querySelector('[data-monitor]').replaceChildren();
        if (document.fullscreenElement === canvas) document.exitFullscreen().catch(() => {});
    }
    clearTimeout(state.timeout);
    state.listeners.forEach(remove => remove()); state.observer.disconnect();
    state.socket.close(); sessions.delete(canvas);
    canvas.getContext("2d").clearRect(0, 0, canvas.width, canvas.height);
    canvas.style.outlineColor = "transparent";
}
