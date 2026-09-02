[CmdletBinding(PositionalBinding = $false)]
param(
    [AllowEmptyString()]
    [string] $BaseCommit,

    [string] $HeadCommit = 'HEAD'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($BaseCommit) -or $BaseCommit -match '^0+$') {
    $BaseCommit = & git rev-parse "$HeadCommit^"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not determine the parent of commit '$HeadCommit'"
    }
}

$diffArguments = @(
    'diff'
    '--name-only'
    '--diff-filter=ACMR'
    $BaseCommit
    $HeadCommit
    '--'
    '*.cs'
    '*.vb'
)

$changedFiles = @(& git @diffArguments)
if ($LASTEXITCODE -ne 0) {
    throw "Could not list source files changed between '$BaseCommit' and '$HeadCommit'"
}

$sourceFiles = @(
    $changedFiles | Where-Object {
        $_ -notmatch '(^|/)(bin|obj|Artifacts)/' -and
        $_ -notmatch '^src/api/' -and
        $_ -notmatch '\.(Designer|generated|g|g\.i|received|verified)\.(cs|vb)$'
    }
)

if ($sourceFiles.Count -eq 0) {
    Write-Host 'No changed C# or Visual Basic source files require whitespace validation.'
    return
}

Write-Host "Checking whitespace formatting for $($sourceFiles.Count) changed source file(s):"
$sourceFiles | ForEach-Object { Write-Host "  $_" }

$formatArguments = @(
    'format'
    'whitespace'
    '--folder'
    '--verify-no-changes'
    '--verbosity'
    'normal'
    '--include'
) + $sourceFiles

& dotnet @formatArguments
if ($LASTEXITCODE -ne 0) {
    throw "Whitespace formatting check failed with exit code $LASTEXITCODE. Run 'dotnet format whitespace --folder --include <changed files>' to apply the required changes."
}
