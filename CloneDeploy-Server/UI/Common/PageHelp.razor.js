// Resolve rendered panels, rather than numeric indexes: feature flags, responsive
// ordering and KeepPanelsAlive can all change which panel is actually visible.
export function observe(reference) {
    let previous = "";
    let pending;
    let focusedPanel;
    const visible = element => {
        for (let node = element; node instanceof HTMLElement; node = node.parentElement) {
            if (node.hidden || getComputedStyle(node).display === "none" ||
                getComputedStyle(node).visibility === "hidden") return false;
        }
        // MudBlazor's active panels use display: contents (no own layout box).
        return element.isConnected;
    };
    const update = () => {
        const candidates = [...document.querySelectorAll("[data-help-href]")]
            .map(marker => ({ marker, panel: marker.closest(".mud-tab-panel") }))
            .filter(item => item.panel && visible(item.panel));
        const depth = element => {
            let value = 0;
            for (; element; element = element.parentElement) value++;
            return value;
        };
        // Prefer the last interacted-with tab group when multiple groups are open.
        const relevant = focusedPanel && visible(focusedPanel)
            ? candidates.filter(item => focusedPanel.contains(item.panel) || item.panel.contains(focusedPanel))
            : [];
        const selected = (relevant.length ? relevant : candidates)
            .sort((a, b) => depth(b.panel) - depth(a.panel))[0]?.marker;
        const href = selected?.dataset.helpHref ?? "";
        const title = selected?.dataset.helpTitle ?? "";
        const key = JSON.stringify([location.href, href, title]);
        if (key === previous) return;
        previous = key;
        reference.invokeMethodAsync("SetTabHelp", href, title).catch(() => {});
    };
    const schedule = () => {
        clearTimeout(pending);
        pending = setTimeout(update, 0);
    };
    const interact = event => {
        const target = event.target;
        if (!(target instanceof Element)) return;
        const panel = target.closest(".mud-tab-panel");
        if (panel) focusedPanel = panel;
        // A tab header belongs to its enclosing group, not its former active panel.
        if (target.closest('[role="tab"]')) focusedPanel = target.closest(".mud-tabs");
        schedule();
    };
    const observer = new MutationObserver(schedule);
    observer.observe(document.body, { subtree: true, childList: true, attributes: true,
        attributeFilter: ["class", "style", "hidden", "data-help-href", "data-help-title"] });
    document.addEventListener("click", interact, true);
    document.addEventListener("focusin", interact, true);
    window.addEventListener("resize", schedule);
    schedule();
    return { dispose() {
        observer.disconnect();
        clearTimeout(pending);
        document.removeEventListener("click", interact, true);
        document.removeEventListener("focusin", interact, true);
        window.removeEventListener("resize", schedule);
    }};
}
