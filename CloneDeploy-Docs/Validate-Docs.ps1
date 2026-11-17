[CmdletBinding()]
param([switch]$Built)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Validate-DialogDocs.ps1') -Built:$Built
# Windows PowerShell 5.1 emits the JSON array as one object; do not wrap it in another array.
$topics = Get-Content (Join-Path $PSScriptRoot 'help-topics.json') -Raw | ConvertFrom-Json
$tabs = Get-Content (Join-Path $PSScriptRoot 'help-tabs.json') -Raw | ConvertFrom-Json
$duplicates = $topics | Group-Object route | Where-Object Count -gt 1
if ($duplicates) { throw "Duplicate help routes: $($duplicates.Name -join ', ')" }

$routes = @(Get-ChildItem (Join-Path $repository 'CloneDeploy-Server/UI') -Filter '*.razor' -Recurse |
    ForEach-Object {
        $source = Get-Content $_.FullName -Raw
        foreach ($match in [regex]::Matches($source, '(?m)^@page\s+"([^"]+)"')) {
            $match.Groups[1].Value
        }
    } | Sort-Object -Unique)

foreach ($route in $routes) {
    if ($route -notin $topics.route) { throw "Missing help topic for UI route: $route" }
}

$actualTabs = @(Get-ChildItem (Join-Path $repository 'CloneDeploy-Server/UI') -Filter '*.razor' -Recurse |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($repository.Length + 1).Replace('\', '/')
        $source = Get-Content $_.FullName -Raw
        $occurrences = @{}
        foreach ($match in [regex]::Matches($source, '<MudTabPanel\b[^>]*\bText="(@\([\s\S]*?\)|@Loc\[(?:"(?:\\.|[^"\\])*"|[^\]\r\n])*\]|[^"]+)"')) {
            $label = $match.Groups[1].Value
            $occurrence = [int]$occurrences[$label]
            $occurrences[$label] = $occurrence + 1
            $remaining = $source.Substring($match.Index + $match.Length)
            $marker = [regex]::Match($remaining, '^[^>]*>\s*(<TabHelp[^\r\n]+/>)')
            if (!$marker.Success) { throw "Missing contextual help marker: $relativePath / $label" }
            [pscustomobject]@{ source = $relativePath; tab = $label; occurrence = $occurrence; marker = $marker.Groups[1].Value }
        }
    })
foreach ($tab in $actualTabs) {
    $matches = @($tabs | Where-Object { $_.source -ceq $tab.source -and $_.tab -ceq $tab.tab })
    if ($matches.Count -gt 1) { $matches = @($matches | Where-Object { [int]$_.occurrence -eq $tab.occurrence }) }
    if ($matches.Count -ne 1) { throw "Expected one help article for tab: $($tab.source) / $($tab.tab)" }
    if (!$tab.marker.Contains('"' + $matches[0].href + '"')) { throw "Help marker destination differs from manifest: $($tab.source) / $($tab.tab)" }
    foreach ($variant in $matches[0].variants) {
        if (!$tab.marker.Contains('"' + $variant.href + '"')) { throw "Missing conditional help destination: $($variant.href)" }
    }
}
foreach ($tab in $tabs) {
    if (!($actualTabs | Where-Object { $_.source -ceq $tab.source -and $_.tab -ceq $tab.tab })) {
        throw "Stale help tab: $($tab.source) / $($tab.tab)"
    }
}

$variants = @($tabs | ForEach-Object { $_.variants } | Where-Object { $_ })
$toc = Get-Content (Join-Path $PSScriptRoot 'toc.yml') -Raw
$duplicateArticles = $tabs | Group-Object href | Where-Object Count -gt 1
if ($duplicateArticles) { throw "Tabs must have distinct articles: $($duplicateArticles.Name -join ', ')" }
foreach ($topic in $topics) {
    if ($topic.navigation -ne $false -and !$toc.Contains($topic.href.Replace('.html', '.md'))) { throw "Page missing from docs navigation: $($topic.route)" }
}
foreach ($tab in @($tabs) + $variants) {
    if (!$toc.Contains($tab.href.Replace('.html', '.md'))) { throw "Tab missing from docs navigation: $($tab.href)" }
    if ($tab.href.Contains('#')) { throw "Tab must have its own help page: $($tab.href)" }
}
foreach ($topic in @($topics) + @($tabs) + $variants) {
    if ($topic.href -notmatch '^(?:[a-z0-9-]+/)*[a-z0-9-]+\.html(#[a-z0-9-]+)?$') {
        throw "Invalid documentation destination: $($topic.href)"
    }

    $parts = $topic.href.Split('#')
    $sourcePath = Join-Path $PSScriptRoot ($parts[0] -replace '\.html$', '.md')
    if (!(Test-Path -LiteralPath $sourcePath)) { throw "Missing article: $sourcePath" }

    if ($Built) {
        $htmlPath = Join-Path (Join-Path $PSScriptRoot '_site') $parts[0]
        if (!(Test-Path -LiteralPath $htmlPath)) { throw "Missing generated page: $htmlPath" }
        if ($parts.Length -gt 1) {
            $html = Get-Content $htmlPath -Raw
            if ($html -notmatch ('id="' + [regex]::Escape($parts[1]) + '"')) {
                throw "Missing generated anchor: $($topic.href)"
            }
        }
    }
}

if ($Built) {
    # The modern template fetches JSON, not toc.html, to render navigation.
    function Test-NavigationItems($items, $directory) {
        foreach ($item in $items) {
            if ($item.href) {
                $target = Join-Path $directory $item.href.Split('#')[0]
                if (!(Test-Path -LiteralPath $target -PathType Leaf)) {
                    throw "JSON navigation destination is missing: $target"
                }
            }
            if ($item.items) { Test-NavigationItems $item.items $directory }
        }
    }
    foreach ($jsonPath in @('toc.json')) {
        $path = Join-Path (Join-Path $PSScriptRoot '_site') $jsonPath
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing browser navigation data: $path"
        }
        $navigation = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if (!$navigation.items) { throw "Empty browser navigation data: $path" }
        Test-NavigationItems $navigation.items (Split-Path $path)
        $webUi = @($navigation.items | Where-Object name -eq 'Web UI')
        if ($webUi.Count -ne 1) { throw 'Expected one Web UI reference section.' }
        $checkedReferences = [System.Collections.Generic.HashSet[string]]::new()
        function Test-WebUiArticles($items) {
            foreach ($item in $items) {
                if ($item.href) {
                    $relative = $item.href.Split('#')[0] -replace '\.html$', '.md'
                    if ($checkedReferences.Add($relative)) {
                        $sourcePath = Join-Path $PSScriptRoot $relative
                        $article = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
                        if ($article -match '(?m)^\|' -or $article -match '(?i)<table\b') {
                            throw "Web UI controls must use bold labels instead of tables: $relative"
                        }
                        if ($article -match '(?im)^#{2,6}\s+(Fields|Actions|Behaviou?r)\s*$') {
                            throw "Web UI controls must be explained directly, without container headings: $relative"
                        }
                        if ($article -match '(?m)^\*\*[^\r\n]+\*\*\s*$' -and
                            !$article.Contains('<div class="ui-controls-spacer"></div>')) {
                            throw "Missing overview spacer before Web UI control descriptions: $relative"
                        }
                        if ($article -match '<!--\s*(Describe|TODO)' -or $article -match '(?m)^\*\*Retry\*\*\s*$') {
                            throw "Unfinished placeholder or routine Retry entry in Web UI reference: $relative"
                        }
                    }
                }
                if ($item.items) { Test-WebUiArticles $item.items }
            }
        }
        Test-WebUiArticles $webUi
        Write-Host "Validated layout for $($checkedReferences.Count) Web UI reference articles."
    }
    $contentFolders = (Get-Content (Join-Path $PSScriptRoot 'docfx.json') -Raw | ConvertFrom-Json).build.content.files
    $articles = @($contentFolders | ForEach-Object {
        if ($_ -like '*/**/*.md') {
            Get-ChildItem (Join-Path $PSScriptRoot $_.Split('/')[0]) -Filter '*.md' -Recurse
        } elseif ($_ -like '*.md') { Get-Item (Join-Path $PSScriptRoot $_) }
    })
    foreach ($article in $articles) {
        $source = Get-Content $article.FullName -Raw
        foreach ($link in [regex]::Matches($source, '\[[^\]]*\]\(([^\s)]+)\)')) {
            $href = $link.Groups[1].Value
            if ($href -match '^[a-z]+:' -or $href.StartsWith('//')) { continue }
            $parts = $href.Split('#')
            if ($parts[0] -match '\.(md|html)$') {
                throw "Article content must be self-contained; move documentation navigation to toc.yml: $($article.Name) -> $href"
            }
            $relativeDirectory = $article.DirectoryName.Substring($PSScriptRoot.Length).TrimStart('\', '/')
            $builtDirectory = Join-Path (Join-Path $PSScriptRoot '_site') $relativeDirectory
            $file = if ($parts[0]) { $parts[0] -replace '\.md$', '.html' } else { $article.BaseName + '.html' }
            $target = Join-Path $builtDirectory $file
            if (!(Test-Path -LiteralPath $target -PathType Leaf)) {
                throw "Broken article link in $($article.Name): $href"
            }
            if ($parts.Length -gt 1) {
                $html = Get-Content $target -Raw
                if ($html -notmatch ('id="' + [regex]::Escape($parts[1]) + '"')) {
                    throw "Broken article anchor in $($article.Name): $href"
                }
            }
        }
    }
    foreach ($tocPath in @('toc.html')) {
        $toc = Get-Content (Join-Path (Join-Path $PSScriptRoot '_site') $tocPath) -Raw
        foreach ($link in [regex]::Matches($toc, '<a\b([^>]*)>(.*?)</a>', 'Singleline')) {
            # DocFX renders intentional name-only TOC section labels as <a>Label</a>.
            # They are headings, not links; continue validating every actual destination.
            if ([string]::IsNullOrWhiteSpace($link.Groups[1].Value) -and
                ![string]::IsNullOrWhiteSpace($link.Groups[2].Value)) { continue }
            if ($link.Groups[1].Value -notmatch '\bhref="([^"]+)"' -or $Matches[1] -eq 'undefined') {
                throw "Navigation item has no destination in ${tocPath}: $($link.Groups[2].Value)"
            }
            $destination = Join-Path (Split-Path (Join-Path (Join-Path $PSScriptRoot '_site') $tocPath)) $Matches[1].Split('#')[0]
            if (!(Test-Path -LiteralPath $destination -PathType Leaf)) {
                throw "Navigation destination is missing: $destination"
            }
        }
    }
}

Write-Host "Validated $($routes.Count) UI routes, $($topics.Count) route mappings and $($tabs.Count) tabs."
