export async function download(container, endpoint) {
    const token = container.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (!token) return window.cloneDeployText("Js.agentinstaller.ReloadThisPageBeforeGeneratingAnInstaller");
    const response = await fetch(endpoint, {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'X-CSRF-TOKEN': token }
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => null);
        return problem?.detail || window.cloneDeployText("Js.agentinstaller.InstallerGenerationFailedCheckYourAdministratorSessionAndTry");
    }
    if (!response.headers.get('content-type')?.includes('application/x-msi'))
        return window.cloneDeployText("Js.agentinstaller.YourSessionMayHaveExpiredSignInAgainAnd");
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = 'CloneDeploy-Agent-x64.msi';
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    return null;
}
