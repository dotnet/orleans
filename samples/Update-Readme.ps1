[CmdletBinding()]
param(
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$samplesRoot = $PSScriptRoot
$manifestPath = Join-Path $samplesRoot 'gallery.json'
$readmePath = Join-Path $samplesRoot 'README.md'
$entries = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$lines = [System.Collections.Generic.List[string]]::new()

$lines.Add('# Orleans Samples')
$lines.Add('')
$lines.Add('This directory is the canonical maintained home of the official Orleans samples.')
$lines.Add('')
$lines.Add('<!-- Generated from gallery.json by Update-Readme.ps1. -->')
$lines.Add('')
$lines.Add('Samples imported from other Microsoft repositories retain their original source repository in the index and in `gallery.json`. Their source licenses are preserved alongside the imported content.')
$lines.Add('')
$lines.Add('## Build and validate')
$lines.Add('')
$lines.Add('From the repository root, run:')
$lines.Add('')
$lines.Add('```powershell')
$lines.Add('pwsh ./samples/Validate-Samples.ps1')
$lines.Add('```')
$lines.Add('')
$lines.Add('The command checks the gallery manifest and builds every project in `Samples.slnx`. External cloud services are required only when running samples which use them, not when compiling.')
$lines.Add('')
$lines.Add('## Featured samples')
$lines.Add('')
$lines.Add('| Sample | Description | Original source |')
$lines.Add('| --- | --- | --- |')

foreach ($entry in $entries | Where-Object featured) {
    $sourceLabel = $entry.sourceRepository -replace '^https://github.com/', ''
    $lines.Add("| [$($entry.title)]($($entry.path)) | $($entry.description) | [$sourceLabel]($($entry.sourceRepository)) |")
}

$lines.Add('')
$lines.Add('## All samples')
$lines.Add('')
$lines.Add('| Sample | Description | Languages | Tags | Original source |')
$lines.Add('| --- | --- | --- | --- | --- |')

foreach ($entry in $entries) {
    $sourceLabel = $entry.sourceRepository -replace '^https://github.com/', ''
    $languages = $entry.languages -join ', '
    $tags = $entry.tags -join ', '
    $lines.Add("| [$($entry.title)]($($entry.path)) | $($entry.description) | $languages | $tags | [$sourceLabel]($($entry.sourceRepository)) |")
}

$content = ($lines -join [Environment]::NewLine) + [Environment]::NewLine

if ($Check) {
    $current = Get-Content -LiteralPath $readmePath -Raw
    if ($current -ne $content) {
        throw 'samples/README.md is out of date. Run samples/Update-Readme.ps1.'
    }

    return
}

[System.IO.File]::WriteAllText($readmePath, $content, [System.Text.UTF8Encoding]::new($false))
