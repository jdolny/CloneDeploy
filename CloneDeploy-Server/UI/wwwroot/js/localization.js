// Only application-owned text is translated. Arguments remain plain text.
(() => {
    const resources = JSON.parse(document.getElementById('cd-localization').textContent);
    window.cloneDeployText = (key, ...args) => (resources[key] ?? key)
        .replace(/\{(\d+)\}/g, (match, index) => index < args.length ? String(args[index] ?? '') : match);
})();
