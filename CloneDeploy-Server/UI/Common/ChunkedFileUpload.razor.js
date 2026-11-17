const uploads = new WeakMap();

export function start(root, receiver, maxFileSize, maximumFileCount, selectedFiles) {
    if (uploads.has(root)) return;
    const availableFiles = Array.from(root.querySelectorAll('input[type="file"]'))
        .flatMap(input => Array.from(input.files ?? []));
    // Closing a chip does not remove the file from the native input's FileList.
    const files = selectedFiles.map(selected => {
        const index = availableFiles.findIndex(file => file.name === selected.name
            && file.size === selected.size && file.lastModified === selected.lastModified);
        if (index < 0) throw new Error(window.cloneDeployText("Js.ChunkedFileUploadrazor.SelectedFileIsNoLongerAvailablePleaseSelectIt"));
        return availableFiles.splice(index, 1)[0];
    });
    if (!files.length) throw new Error(window.cloneDeployText("Js.ChunkedFileUploadrazor.SelectFilesFirst"));
    if (files.length > maximumFileCount || files.some(file => file.size <= 0 || file.size > maxFileSize))
        throw new Error(window.cloneDeployText("Js.ChunkedFileUploadrazor.SelectionExceedsTheFileCountOrSizeLimitOr"));
    const state = { controller: new AbortController(), finishing: false };
    uploads.set(root, state);
    void uploadBatch(root, files, receiver, maxFileSize, state);
}

async function uploadBatch(root, files, receiver, maxFileSize, state) {
    let error = null;
    try {
        for (let index = 0; index < files.length; index++) {
            state.controller.signal.throwIfAborted();
            state.finishing = false;
            await receiver.invokeMethodAsync("UploadStarted", files[index].name, index + 1, files.length);
            if (!await upload(root, files[index], receiver, maxFileSize, state)) break;
        }
    } catch (exception) {
        error = exception.name === "AbortError" ? window.cloneDeployText("Js.ChunkedFileUploadrazor.UploadCancelled") : window.cloneDeployText("Js.ChunkedFileUploadrazor.UploadStopped") + exception.message;
    } finally {
        uploads.delete(root);
        root.querySelectorAll('input[type="file"]').forEach(input => { input.value = ""; });
        await receiver.invokeMethodAsync("BatchFinished", error).catch(() => {});
    }
}

export function cancel(root) {
    const state = uploads.get(root);
    if (state && !state.finishing) state.controller.abort();
}

async function upload(root, file, receiver, maxFileSize, state) {
    const base = new URL("api/ui/uploads", document.baseURI).href;
    let token;
    let id;
    let result = null;
    let error = null;
    async function request(url, options = {}) {
        const response = await fetch(url, {
            credentials: "same-origin",
            redirect: "error",
            signal: state.controller.signal,
            ...options,
            headers: { "X-CSRF-TOKEN": token ?? "", ...options.headers }
        });
        if (!response.ok) {
            const problem = await response.json().catch(() => null);
            throw new Error(problem?.detail ?? window.cloneDeployText("Js.ChunkedFileUploadrazor.UploadRequestFailed0", response.status));
        }
        return response.status === 204 ? null : response.json();
    }
    try {
        if (file.size <= 0 || file.size > maxFileSize) throw new Error(window.cloneDeployText("Js.ChunkedFileUploadrazor.FileExceedsTheAllowedSizeOrIsEmpty"));
        ({ token } = await request(`${base}/token`));
        const session = await request(base, {
            method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ fileName: file.name, size: file.size })
        });
        id = session.id;
        for (let offset = 0; offset < file.size; offset += session.chunkSize) {
            const end = Math.min(offset + session.chunkSize, file.size);
            await request(`${base}/${id}/chunks?offset=${offset}`, {
                method: "PUT", headers: { "Content-Type": "application/octet-stream" },
                body: file.slice(offset, end)
            });
            await receiver.invokeMethodAsync("ReportProgress", end / file.size * 100, false);
        }
        state.controller.signal.throwIfAborted();
        state.finishing = true;
        await receiver.invokeMethodAsync("ReportProgress", 100, true);
        result = await request(`${base}/${id}/complete`, { method: "POST" });
    } catch (exception) {
        error = exception.name === "AbortError" ? window.cloneDeployText("Js.ChunkedFileUploadrazor.UploadCancelled") : exception.message;
    } finally {
        if (id && !result) {
            // Separate signal: cancellation of the upload must not cancel cleanup.
            await fetch(`${base}/${id}`, {
                method: "DELETE", credentials: "same-origin", redirect: "error",
                headers: { "X-CSRF-TOKEN": token }, signal: AbortSignal.timeout(10000)
            }).catch(() => {});
        }
        state.finishing = false;
    }
    await receiver.invokeMethodAsync("UploadFinished", result, error);
    return result !== null;
}
