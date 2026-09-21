[CmdletBinding()]
param(
    [string] $BaselineDirectory = '.dissemination-baseline',
    [string] $ArtifactsDirectory = 'Artifacts/DisseminationCompatibility',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$baselineSha = '6739589254b746a8790cf53524e6abe372bb53d4'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root $ArtifactsDirectory))
$binaries = Join-Path $artifacts 'bin'
$results = Join-Path $artifacts 'results'
New-Item -ItemType Directory -Force $binaries, $results | Out-Null

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')"
    }
}

function Get-Revision([string] $Directory) {
    $revision = & git -C $Directory rev-parse HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Not a source checkout: $Directory"
    }
    return $revision.Trim()
}

function Publish-Silo([string] $Checkout, [string] $Runtime, [string] $Revision) {
    $project = Join-Path $Checkout 'test/Dissemination.IntegrationHarness/Silo/Dissemination.Silo.csproj'
    $output = Join-Path $binaries $Runtime
    Invoke-DotNet @(
        'publish', $project, '--configuration', 'Release', '--framework', 'net10.0',
        '--output', $output, '--verbosity', 'minimal',
        "-p:HarnessRuntime=$Runtime", "-p:SourceRevisionId=$Revision",
        '-p:OfficialBuild=true', "-p:BUILD_SOURCEVERSION=$Revision",
        "-bl:$(Join-Path $artifacts "build-$Runtime.binlog")"
    )
    $assemblyPath = Join-Path $output 'Orleans.Runtime.dll'
    $identity = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
    $assemblies = @{}
    foreach ($file in Get-ChildItem -LiteralPath $output -Filter 'Orleans*.dll' -File) {
        $name = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        $assemblies[$name.Name] = @{
            AssemblyVersion = $name.Version.ToString()
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    @{
        Runtime = $Runtime
        SourceRevision = $Revision
        AssemblyName = $identity.Name
        AssemblyVersion = $identity.Version.ToString()
        Sha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
        Assemblies = $assemblies
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json')
}

if (!$SkipBuild) {
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw 'Building the pinned baseline is CI-only. Download the workflow binary artifact and use -SkipBuild for explicit local reproduction.'
    }

    $baseline = [System.IO.Path]::GetFullPath((Join-Path $root $BaselineDirectory))
    if ($baseline -eq $root) {
        throw 'The baseline must not be the feature checkout.'
    }
    $baselineTopLevel = (& git -C $baseline rev-parse --show-toplevel)
    if ($LASTEXITCODE -ne 0 -or [System.IO.Path]::GetFullPath($baselineTopLevel.Trim()) -ne $baseline) {
        throw 'The baseline must have its own isolated git checkout, not merely a directory in the feature checkout.'
    }
    if ((Get-Revision $baseline) -ne $baselineSha) {
        throw "Baseline must be pinned to $baselineSha."
    }
    $sourceChanges = & git -C $baseline status --porcelain --untracked-files=all -- src
    if ($LASTEXITCODE -ne 0 -or $sourceChanges) {
        throw 'Pinned baseline runtime sources have local modifications.'
    }
    if (Test-Path -LiteralPath (Join-Path $baseline 'src/Orleans.Runtime/Dissemination')) {
        throw 'The pinned baseline unexpectedly contains the new dissemination implementation.'
    }

    # Only the test executable is copied. Every Orleans source project resolves inside its own checkout.
    $harnessSource = Join-Path $root 'test/Dissemination.IntegrationHarness'
    $harnessDestination = Join-Path $baseline 'test/Dissemination.IntegrationHarness'
    if (Test-Path -LiteralPath $harnessDestination) {
        throw "Refusing to overwrite an existing baseline harness: $harnessDestination"
    }
    New-Item -ItemType Directory -Force $harnessDestination | Out-Null
    foreach ($part in @('Silo', 'Shared')) {
        New-Item -ItemType Directory -Force (Join-Path $harnessDestination $part) | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $harnessSource $part) -File |
            Where-Object { $_.Extension -in '.cs', '.csproj' } |
            Copy-Item -Destination (Join-Path $harnessDestination $part)
    }

    Publish-Silo $baseline 'Old' $baselineSha
    Publish-Silo $root 'New' (Get-Revision $root)
    Invoke-DotNet @(
        'publish', (Join-Path $root 'test/Dissemination.IntegrationHarness/Tests/Dissemination.IntegrationHarness.Tests.csproj'),
        '--configuration', 'Release', '--framework', 'net10.0',
        '--output', (Join-Path $binaries 'Runner'), '--verbosity', 'minimal',
        "-bl:$(Join-Path $artifacts 'build-runner.binlog')"
    )
}

$env:ORLEANS_DISSEMINATION_OLD = Join-Path $binaries 'Old'
$env:ORLEANS_DISSEMINATION_NEW = Join-Path $binaries 'New'
$env:ORLEANS_DISSEMINATION_RESULTS = $results
$runner = Join-Path $binaries 'Runner/Dissemination.IntegrationHarness.Tests.dll'
if (!(Test-Path -LiteralPath $runner)) {
    throw "Published runner not found: $runner"
}
$testClass = 'Orleans.Dissemination.IntegrationHarness.VersionSkewTests'
$minimum = '10'

@{
    PinnedBaseline = $baselineSha
    CurrentCommit = Get-Revision $root
    StartUtc = [DateTimeOffset]::UtcNow
    Command = "dotnet $runner --filter-class $testClass --minimum-expected-tests $minimum --report-trx"
    Environment = 'Separate loopback OS processes with a controlled test membership provider.'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'invocation.json')

Push-Location $root
try {
    Invoke-DotNet @(
        $runner, '--filter-class', 'Orleans.Dissemination.IntegrationHarness.CompatibilityHarnessTests',
        '--minimum-expected-tests', '8', '--report-trx',
        '--results-directory', (Join-Path $results 'harness-checks')
    )

    Invoke-DotNet @(
        $runner, '--filter-class', $testClass, '--minimum-expected-tests', $minimum,
        '--report-trx', '--results-directory', $results
    )
}
finally {
    Pop-Location
}
