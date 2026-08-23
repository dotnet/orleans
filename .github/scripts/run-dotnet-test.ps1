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

function Assert-NotReparsePoint {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

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

if ($env:GITHUB_EVENT_NAME -ne 'pull_request') {
    if ($NoBuild) {
        $testArguments.Add('--no-build')
    }

    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    return
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$coverageSettings = Join-Path $repositoryRoot '.github/coverage.config.xml'
$coverageDirectory = Join-Path $repositoryRoot 'TestResults'
$coverageOutput = Join-Path $coverageDirectory "$CoverageId.cobertura.xml"
Assert-NotReparsePoint $coverageDirectory
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
Assert-NotReparsePoint $coverageOutput
Remove-Item -LiteralPath $coverageOutput -Force -ErrorAction SilentlyContinue

$buildArguments = [Collections.Generic.List[string]]::new()
$buildArguments.Add('build')
if (-not [string]::IsNullOrWhiteSpace($Project)) {
    $buildArguments.Add($Project)
}
$buildArguments.Add('--framework')
$buildArguments.Add($Framework)
$buildArguments.Add('-p:ContinuousIntegrationBuild=false')
if (-not $NoBuild) {
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage build failed with exit code $LASTEXITCODE"
    }
}

$testArguments.Add('--no-build')
$coverageTool = (Get-Command dotnet-coverage -ErrorAction Stop).Source

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
