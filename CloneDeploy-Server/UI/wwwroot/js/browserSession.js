let redirecting = false;

export async function ensureSession(expectedUserId) {
    const response = await fetch(new URL('api/ui/browser-session', document.baseURI), {
        credentials: 'same-origin', cache: 'no-store', redirect: 'error',
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
    let stopped = false, checking = false;
    const check = async () => {
        if (stopped || checking) return;
        checking = true;
        try { await ensureSession(expectedUserId); }
        catch { /* A network outage is not an expired login. Retry on the next check. */ }
        finally { checking = false; }
    };
    const visible = () => { if (document.visibilityState === 'visible') void check(); };
    const timer = setInterval(check, 60000);
    document.addEventListener('visibilitychange', visible);
    window.addEventListener('focus', check);
    void check();
    return { dispose() {
        stopped = true;
        clearInterval(timer);
        document.removeEventListener('visibilitychange', visible);
        window.removeEventListener('focus', check);
    } };
}
