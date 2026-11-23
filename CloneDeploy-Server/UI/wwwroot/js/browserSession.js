let redirecting = false;

export async function ensureSession(expectedUserId, active = false) {
    const response = await fetch(new URL('api/ui/browser-session', document.baseURI), {
        credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: active ? { 'X-CloneDeploy-User-Activity': '1' } : {},
        signal: AbortSignal.timeout(10000)
    });
    if (response.status === 401) {
        if (!redirecting) {
            redirecting = true;
            const login = new URL('login', document.baseURI);
            login.searchParams.set('error', 'session-expired');
            login.searchParams.set('returnUrl', location.pathname + location.search + location.hash);
            location.assign(login.href);
        }
        return false;
    }
    if (!response.ok) throw new Error(window.cloneDeployText("Js.browserSession.CouldNotVerifyYourSignInPleaseTryAgain"));
    const session = await response.json();
    if (expectedUserId && session.userId !== expectedUserId) {
        // Another tab signed in as a different user: discard the old circuit identity.
        location.reload();
        return false;
    }
    return true;
}

export function watch(expectedUserId) {
    let stopped = false, checking = false, active = false, lastCheck = 0;
    const check = async () => {
        if (stopped || checking) return;
        checking = true;
        lastCheck = Date.now();
        const renew = active && document.visibilityState === 'visible';
        active = false;
        try { await ensureSession(expectedUserId, renew); }
        catch { /* A network outage is not an expired login. Retry on the next check. */ }
        finally { checking = false; }
    };
    // Blazor events use the existing circuit and cannot renew a browser cookie.
    // Send a throttled HTTP check after actual input; timers/focus alone stay passive.
    const activity = event => {
        if (!event.isTrusted || document.visibilityState !== 'visible') return;
        active = true;
        if (Date.now() - lastCheck >= 60000) void check();
    };
    const events = ['pointerdown', 'keydown', 'wheel', 'touchstart'];
    for (const name of events) document.addEventListener(name, activity, { passive: true });
    const visible = () => { if (document.visibilityState === 'visible') void check(); };
    const timer = setInterval(check, 60000);
    document.addEventListener('visibilitychange', visible);
    window.addEventListener('focus', check);
    void check();
    return { dispose() {
        stopped = true;
        clearInterval(timer);
        for (const name of events) document.removeEventListener(name, activity);
        document.removeEventListener('visibilitychange', visible);
        window.removeEventListener('focus', check);
    } };
}
