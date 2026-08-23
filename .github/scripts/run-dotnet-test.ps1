[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string] $CoverageId,

    [Parameter(Mandatory)]
    [string] $Framework,

    [string] $Project,

    [string] $FilterQuery,

    [Parameter(Mandatory)]
    [string] $ReportTrxFilename,

    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testArguments = [Collections.Generic.List[string]]::new()
$testArguments.Add('test')
if (-not [string]::IsNullOrWhiteSpace($Project)) {
    $testArguments.Add('--project')
    $testArguments.Add($Project)
}
$testArguments.Add('--framework')
$testArguments.Add($Framework)
if (-not [string]::IsNullOrWhiteSpace($FilterQuery)) {
    $testArguments.Add('--filter-query')
    $testArguments.Add($FilterQuery)
}
$testArguments.Add('--minimum-expected-tests')
$testArguments.Add('1')
$testArguments.Add('--hangdump')
$testArguments.Add('--hangdump-timeout')
$testArguments.Add('10m')
$testArguments.Add('--crashdump')
$testArguments.Add('--crashdump-type')
$testArguments.Add('Full')
$testArguments.Add('--hangdump-type')
$testArguments.Add('Full')
$testArguments.Add('--report-trx')
$testArguments.Add('--report-trx-filename')
$testArguments.Add($ReportTrxFilename)
$testArguments.Add('--max-parallel-test-modules')
$testArguments.Add('1')
if ($NoBuild) {
    $testArguments.Add('--no-build')
}

if ($env:GITHUB_EVENT_NAME -ne 'pull_request') {
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    return
}

$coverageTool = (Get-Command dotnet-coverage -ErrorAction Stop).Source
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$coverageSettings = Join-Path $repositoryRoot '.github/coverage.config.xml'
$coverageDirectory = Join-Path $repositoryRoot 'TestResults'
$coverageOutput = Join-Path $coverageDirectory "$CoverageId.cobertura.xml"
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
Remove-Item -LiteralPath $coverageOutput -Force -ErrorAction SilentlyContinue

& $coverageTool collect `
    --settings $coverageSettings `
    --output $coverageOutput `
    --output-format cobertura `
    --nologo `
    dotnet @testArguments
if ($LASTEXITCODE -ne 0) {
    throw "Coverage test run failed with exit code $LASTEXITCODE"
}

$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 100MB
$reader = [Xml.XmlReader]::Create($coverageOutput, $settings)
try {
    $coverage = [Xml.XmlDocument]::new()
    $coverage.XmlResolver = $null
    $coverage.Load($reader)
    if (-not $coverage.SelectSingleNode('//*[local-name()="line"]')) {
        throw "$coverageOutput contains no measured lines"
    }
} finally {
    $reader.Dispose()
}
