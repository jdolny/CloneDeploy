[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$validator = Join-Path (Split-Path $PSScriptRoot -Parent) 'Validate-DialogDocs.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = Join-Path $temporaryRoot ('clonedeploy-dialog-doc-tests-' + [guid]::NewGuid().ToString('N'))
$ui = Join-Path $fixture 'CloneDeploy-Server/UI'
$docs = Join-Path $fixture 'CloneDeploy-Docs'
$sourceName = 'CloneDeploy-Server/UI/ExampleDialog.razor'
$passed = 0

function Reset-Fixture {
    Set-Content (Join-Path $ui 'ExampleDialog.razor') '<MudDialog><ChildForm /><MudButton>Save</MudButton></MudDialog>' -Encoding UTF8
    Set-Content (Join-Path $ui 'ChildForm.razor') '<MudTextField Label="@Loc[LocKeys.Test.Name]" /><MudSwitch Label="Conditional setting" />' -Encoding UTF8
    Set-Content (Join-Path $docs 'toc.yml') "- name: Example`n  href: ui/example.md" -Encoding UTF8
    Set-Content (Join-Path $docs 'ui/example.md') "# Example`nName identifies the item. Conditional setting controls availability. Save commits changes." -Encoding UTF8
    ConvertTo-Json -InputObject @(@{ source = $sourceName; kind = 'configuration'; dialogs = 1; href = 'ui/example.html'; requiredText = @('Save') }) -Depth 5 |
        Set-Content (Join-Path $docs 'help-dialogs.json') -Encoding UTF8
}

function Assert-Rejected([string]$name, [string]$expected) {
    $message = $null
    try { & $validator -RepositoryRoot $fixture -DocsRoot $docs } catch { $message = $_.Exception.Message }
    if (!$message -or !$message.Contains($expected)) { throw "${name}: expected '$expected', got '$message'" }
    $script:passed++
    Reset-Fixture
}

try {
    [void](New-Item -ItemType Directory -Path $ui, (Join-Path $fixture 'CloneDeploy-Server/Localization'), (Join-Path $docs 'ui') -Force)
    Set-Content (Join-Path $fixture 'CloneDeploy-Server/Localization/LocalizationResources.resx') '<root><data name="Test.Name"><value>Name</value></data></root>' -Encoding UTF8
    Reset-Fixture
    & $validator -RepositoryRoot $fixture -DocsRoot $docs
    $passed++

    Set-Content (Join-Path $ui 'SecondDialog.razor') '<MudDialog><MudButton>Save</MudButton></MudDialog>' -Encoding UTF8
    $firstEntry = (Get-Content (Join-Path $docs 'help-dialogs.json') -Raw | ConvertFrom-Json)[0]
    $secondEntry = @{ source = 'CloneDeploy-Server/UI/SecondDialog.razor'; kind = 'configuration'; dialogs = 1; href = 'ui/example.html'; requiredText = @('Save') }
    ConvertTo-Json -InputObject @($firstEntry, $secondEntry) -Depth 5 | Set-Content (Join-Path $docs 'help-dialogs.json') -Encoding UTF8
    & $validator -RepositoryRoot $fixture -DocsRoot $docs
    $passed++
    Remove-Item -LiteralPath (Join-Path $ui 'SecondDialog.razor')
    Reset-Fixture

    Set-Content (Join-Path $ui 'NewDialog.razor') '<MudDialog />' -Encoding UTF8
    Assert-Rejected 'New dialog' 'Missing dialog documentation mapping'
    Remove-Item -LiteralPath (Join-Path $ui 'NewDialog.razor')

    Set-Content (Join-Path $ui 'ExampleDialog.razor') '<MudDialog /><MudDialog />' -Encoding UTF8
    Assert-Rejected 'New inline dialog' 'Dialog count changed'

    Set-Content (Join-Path $docs 'ui/example.md') 'Name and Save are documented, but the other field is absent.' -Encoding UTF8
    Assert-Rejected 'Conditional embedded field' "Undocumented dialog control 'Conditional setting'"

    Add-Content (Join-Path $ui 'ChildForm.razor') '<MudTextField Label="New conditional field" />' -Encoding UTF8
    Assert-Rejected 'Added embedded field' "Undocumented dialog control 'New conditional field'"

    $entry = Get-Content (Join-Path $docs 'help-dialogs.json') -Raw | ConvertFrom-Json
    $entry[0].requiredText = @()
    ConvertTo-Json -InputObject @($entry) -Depth 5 | Set-Content (Join-Path $docs 'help-dialogs.json') -Encoding UTF8
    Assert-Rejected 'Empty control checks' 'Dialog needs documented control/action checks'

    Set-Content (Join-Path $docs 'toc.yml') '- name: Empty' -Encoding UTF8
    Assert-Rejected 'Undiscoverable documentation' 'Dialog article missing from navigation'

    Remove-Item -LiteralPath (Join-Path $docs 'ui/example.md')
    Assert-Rejected 'Deleted article' 'Missing dialog article'

    Set-Content (Join-Path $ui 'ExampleDialog.razor') '<p>Dialog removed</p>' -Encoding UTF8
    Assert-Rejected 'Stale mapping' 'Stale dialog documentation mapping'

    try { & $validator -RepositoryRoot $fixture -DocsRoot $docs -Built; throw 'Expected generated-page failure.' }
    catch { if (!$_.Exception.Message.Contains('Missing generated dialog article')) { throw } }
    $passed++
    Write-Host "Passed $passed dialog documentation validation tests."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'clonedeploy-dialog-doc-tests-*') {
        throw 'Refusing cleanup outside the temporary test fixture.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
