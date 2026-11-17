[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$DocsRoot,
    [switch]$Built
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path $PSScriptRoot -Parent }
if ([string]::IsNullOrWhiteSpace($DocsRoot)) { $DocsRoot = $PSScriptRoot }
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$DocsRoot = [IO.Path]::GetFullPath($DocsRoot)
# Windows PowerShell 5.1 emits the JSON array as one object; do not wrap it again.
$entries = Get-Content (Join-Path $DocsRoot 'help-dialogs.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$toc = Get-Content (Join-Path $DocsRoot 'toc.yml') -Raw -Encoding UTF8
$navigation = @([regex]::Matches($toc, '(?m)^\s*href:\s*["'']?([^\s"''#]+)') | ForEach-Object { $_.Groups[1].Value })
$files = @(Get-ChildItem (Join-Path $RepositoryRoot 'CloneDeploy-Server/UI') -Filter '*.razor' -Recurse)
$sources = @{}
$components = @{}
$actual = @{}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($RepositoryRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    $source = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $sources[$relative] = $source
    $components[$file.BaseName] = @($components[$file.BaseName]) + @($relative)
    $count = [regex]::Matches($source, '<MudDialog\b').Count
    if ($count) { $actual[$relative] = $count }
}
$duplicates = @($entries | Group-Object source | Where-Object Count -gt 1)
if ($duplicates.Count) { throw "Duplicate dialog coverage entries: $($duplicates.Name -join ', ')" }
foreach ($source in $actual.Keys) {
    if ($source -cnotin $entries.source) { throw "Missing dialog documentation mapping: $source" }
}

[xml]$resources = Get-Content (Join-Path $RepositoryRoot 'CloneDeploy-Server/Localization/LocalizationResources.resx') -Raw -Encoding UTF8
$labels = @{}
foreach ($resource in $resources.root.data) { $labels[[string]$resource.name] = [string]$resource.value }

# Include controls rendered by shared forms, rather than checking only the dialog wrapper.
function Get-DialogForms([string]$source, $seen) {
    if (!$seen.Add($source)) { return }
    $markup = ($sources[$source] -split '@code', 2)[0]
    foreach ($tag in [regex]::Matches($markup, '<([A-Z][A-Za-z0-9_]+)\b')) {
        foreach ($child in $components[$tag.Groups[1].Value]) {
            if ($child) { Get-DialogForms $child $seen }
        }
    }
}

$fieldCount = 0
$dialogCount = 0
foreach ($entry in $entries) {
    if (!$actual.ContainsKey($entry.source)) { throw "Stale dialog documentation mapping: $($entry.source)" }
    if ($entry.kind -notin @('configuration', 'selection', 'viewer', 'confirmation')) { throw "Invalid dialog kind: $($entry.source)" }
    if ($entry.dialogs -ne $actual[$entry.source]) { throw "Dialog count changed; review documentation: $($entry.source)" }
    if (!@($entry.requiredText | Where-Object { ![string]::IsNullOrWhiteSpace($_) }).Count) { throw "Dialog needs documented control/action checks: $($entry.source)" }
    $dialogCount += $entry.dialogs
    $forms = [System.Collections.Generic.HashSet[string]]::new()
    Get-DialogForms $entry.source $forms
    $terms = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($term in $entry.requiredText) { [void]$terms.Add($term) }
    foreach ($form in $forms) {
        $markup = ($sources[$form] -split '@code', 2)[0]
        foreach ($attribute in [regex]::Matches($markup, '(?<![\w-])Label="([^"]*)"')) {
            $value = $attribute.Groups[1].Value
            if (!$value.StartsWith('@') -and $value) { [void]$terms.Add($value) }
            foreach ($key in [regex]::Matches($value, 'LocKeys\.([A-Za-z0-9_.]+)')) {
                $name = $key.Groups[1].Value
                if (!$labels.ContainsKey($name)) { throw "Unknown dialog label resource: $name" }
                # Per-item formatted labels need the explicit requiredText checks above.
                if (!$labels[$name].Contains('{')) { [void]$terms.Add($labels[$name]) }
            }
        }
        foreach ($key in [regex]::Matches($markup, '\bLabel="@Loc\["([^"]+)"\]')) {
            $name = $key.Groups[1].Value
            if (!$labels.ContainsKey($name)) { throw "Unknown dialog label resource: $name" }
            if (!$labels[$name].Contains('{')) { [void]$terms.Add($labels[$name]) }
        }
    }
    foreach ($href in @($entry.href) + @($entry.additionalHrefs)) {
        if (!$href) { continue }
        if ($href -notmatch '^ui/(?:[a-z0-9-]+/)*[a-z0-9-]+\.html$') { throw "Invalid dialog documentation destination: $href" }
        $relative = $href -replace '\.html$', '.md'
        $path = Join-Path $DocsRoot $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing dialog article: $relative" }
        if ($relative -cnotin $navigation) { throw "Dialog article missing from navigation: $relative" }
        $article = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        foreach ($term in $terms) {
            if ($article.IndexOf($term, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Undocumented dialog control '$term': $($entry.source) -> $relative"
            }
            $fieldCount++
        }
        if ($Built -and !(Test-Path -LiteralPath (Join-Path (Join-Path $DocsRoot '_site') $href) -PathType Leaf)) {
            throw "Missing generated dialog article: $href"
        }
    }
}
Write-Host "Validated $dialogCount dialog definitions in $($entries.Count) components ($fieldCount documentation checks, including embedded forms)."
