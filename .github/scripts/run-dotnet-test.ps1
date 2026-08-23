[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string] $CoverageId,

    [Parameter(Mandatory)]
    [string] $Framework,

    [string] $Project,

    [string] $TestModule,

    [string] $FilterQuery,

    [Parameter(Mandatory)]
    [string] $ReportTrxFilename,

    [switch] $UseStaticInstrumentation,

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
$usesTestModule = -not [string]::IsNullOrWhiteSpace($TestModule)
$testWorkingDirectory = $null
if ($usesTestModule) {
    $resolvedTestModule = (Resolve-Path -LiteralPath $TestModule).Path
    $testWorkingDirectory = [IO.Path]::GetDirectoryName($resolvedTestModule)
    $testArguments.Add('exec')
    $testArguments.Add($resolvedTestModule)
} else {
    $testArguments.Add('test')
    if (-not [string]::IsNullOrWhiteSpace($Project)) {
        $testArguments.Add('--project')
        $testArguments.Add($Project)
    }
    $testArguments.Add('--framework')
    $testArguments.Add($Framework)
}
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
if (-not $usesTestModule) {
    $testArguments.Add('--max-parallel-test-modules')
    $testArguments.Add('1')
}

if ($env:GITHUB_EVENT_NAME -ne 'pull_request') {
    if ($NoBuild -and -not $usesTestModule) {
        $testArguments.Add('--no-build')
    }

    try {
        if ($testWorkingDirectory) {
            Push-Location $testWorkingDirectory
        }
        & dotnet @testArguments
        $testExitCode = $LASTEXITCODE
    } finally {
        if ($testWorkingDirectory) {
            Pop-Location
        }
    }
    if ($testExitCode -ne 0) {
        throw "dotnet test failed with exit code $testExitCode"
    }

    return
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$useStaticInstrumentation = $IsMacOS -or $UseStaticInstrumentation
$coverageSettings = Join-Path $repositoryRoot $(if ($useStaticInstrumentation) { '.github/coverage.static.config.xml' } else { '.github/coverage.config.xml' })
$coverageDirectory = Join-Path $repositoryRoot 'TestResults'
$coverageOutput = Join-Path $coverageDirectory "$CoverageId.cobertura.xml"
Assert-NotReparsePoint $coverageDirectory
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
Assert-NotReparsePoint $coverageOutput
Remove-Item -LiteralPath $coverageOutput -Force -ErrorAction SilentlyContinue

$staticInstrumentationFiles = $null
if ($useStaticInstrumentation) {
    $buildArguments = [Collections.Generic.List[string]]::new()
    $buildArguments.Add('build')
    if (-not [string]::IsNullOrWhiteSpace($Project)) {
        $buildArguments.Add($Project)
        $buildArguments.Add('--framework')
        $buildArguments.Add($Framework)
    }
    $buildArguments.Add('-p:ContinuousIntegrationBuild=false')
    if (-not $NoBuild) {
        & dotnet @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Coverage build failed with exit code $LASTEXITCODE"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Project)) {
        $projectDirectory = [IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $Project).Path)
        $staticInstrumentationFiles = Join-Path $projectDirectory "bin/Debug/$Framework/*.dll"
    } else {
        $staticInstrumentationFiles = Join-Path $repositoryRoot "test/**/bin/Debug/$Framework/*.dll"
    }

    $testArguments.Add('--no-build')
} elseif ($NoBuild) {
    $testArguments.Add('--no-build')
} elseif (-not $usesTestModule) {
    $testArguments.Add('-p:ContinuousIntegrationBuild=false')
}

$coverageTool = (Get-Command dotnet-coverage -ErrorAction Stop).Source

$coverageArguments = [Collections.Generic.List[string]]::new()
$coverageArguments.Add('collect')
$coverageArguments.Add('--settings')
$coverageArguments.Add($coverageSettings)
$coverageArguments.Add('--output')
$coverageArguments.Add($coverageOutput)
$coverageArguments.Add('--output-format')
$coverageArguments.Add('cobertura')
$coverageArguments.Add('--nologo')
if ($staticInstrumentationFiles) {
    $coverageArguments.Add("--include-files=$staticInstrumentationFiles")
}
$coverageArguments.Add('dotnet')

try {
    if ($testWorkingDirectory) {
        Push-Location $testWorkingDirectory
    }
    & $coverageTool @coverageArguments @testArguments
    $testExitCode = $LASTEXITCODE
} finally {
    if ($testWorkingDirectory) {
        Pop-Location
    }
}
if ($testExitCode -ne 0) {
    throw "Coverage test run failed with exit code $testExitCode"
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
