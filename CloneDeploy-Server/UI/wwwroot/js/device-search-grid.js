// SelectOnRowClick disables selection, but MudDataGrid still handles row clicks
// and renders again. Keep plain cells inert before Blazor's delegated listener.
document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const row = event.target.closest('.device-search-grid .mud-table-body .mud-table-row');
    if (!row) return;

    const control = event.target.closest('a, button, input, select, textarea, label, [role="button"], [role="checkbox"]');
    if (control && row.contains(control)) return;

    event.stopPropagation();
}, { capture: true });
