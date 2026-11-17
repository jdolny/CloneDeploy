// DocFX expands and scrolls to the active article on each page load. Keep a
// clicked sidebar link at the same visible position after that work finishes.
const storageKey = 'clonedeploy.docs.navigation';

export default {
    start() {
        const toc = document.getElementById('toc');
        if (!toc) return;

        toc.addEventListener('click', event => {
            const link = event.target.closest('a[href]');
            const scroller = toc.querySelector('.overflow-y-auto');
            if (!link || !scroller || event.defaultPrevented || event.button !== 0 ||
                event.ctrlKey || event.metaKey || event.shiftKey || event.altKey ||
                link.target === '_blank' || link.getAttribute('href') === '#') return;
            const destination = new URL(link.href);
            if (destination.origin !== location.origin ||
                destination.pathname === location.pathname) return;
            try {
                sessionStorage.setItem(storageKey, JSON.stringify({
                    destination: destination.href,
                    offset: link.getBoundingClientRect().top - scroller.getBoundingClientRect().top
                }));
            } catch { /* Navigation still works when browser storage is blocked. */ }
        });

        let saved;
        try {
            saved = JSON.parse(sessionStorage.getItem(storageKey));
            sessionStorage.removeItem(storageKey);
        } catch { return; }
        if (!saved || saved.destination !== location.href || !Number.isFinite(saved.offset)) return;

        const deadline = performance.now() + 10000;
        function restore() {
            if (!window.docfx?.ready) {
                if (performance.now() < deadline) requestAnimationFrame(restore);
                return;
            }
            const scroller = toc.querySelector('.overflow-y-auto');
            const link = Array.from(toc.querySelectorAll('a[href]'))
                .find(item => item.href === saved.destination);
            if (scroller && link) {
                scroller.scrollTop += link.getBoundingClientRect().top -
                    scroller.getBoundingClientRect().top - saved.offset;
            }
        }
        requestAnimationFrame(restore);
    }
};
