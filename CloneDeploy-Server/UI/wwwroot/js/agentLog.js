const viewers = new WeakMap();

export function follow(element) {
    if (!element) return;
    let state = viewers.get(element);
    if (!state) {
        state = { following: true };
        viewers.set(element, state);
        element.addEventListener("scroll", () => {
            state.following = element.scrollHeight - element.clientHeight - element.scrollTop < 40;
        }, { passive: true });
    }
    if (state.following) element.scrollTop = element.scrollHeight;
}

export async function download(reference, name) {
    const blob = new Blob([await reference.arrayBuffer()], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
