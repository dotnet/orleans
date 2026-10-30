[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans build tests $([guid]::NewGuid())"
$shell = (Get-Process -Id $PID).Path
$originalConfiguration = $env:BuildConfiguration
$originalFlags = $env:BUILD_FLAGS
$testsRun = 0

function Assert-Equal {
    param($Expected, $Actual, [string] $Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Invoke-BuildCase {
    param([string[]] $Arguments = @(), [int] $ExpectedExitCode = 0)
    $logPath = Join-Path $temporaryRoot 'calls.jsonl'
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath
    }

    $originalErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $shell -NoProfile -File (Join-Path $temporaryRoot 'run.ps1') @Arguments > $null 2> (Join-Path $temporaryRoot 'stderr.txt')
    } finally {
        $ErrorActionPreference = $originalErrorAction
    }
    Assert-Equal $ExpectedExitCode $LASTEXITCODE 'Script exit code.'
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath | ForEach-Object { $_ | ConvertFrom-Json }
    }
}

function Assert-Commands {
    param($Calls, [string] $Expected)
    Assert-Equal $Expected (($Calls | ForEach-Object { $_.Command }) -join ',') 'Command sequence.'
    foreach ($call in $Calls) {
        Assert-Equal (Join-Path $temporaryRoot 'Orleans.slnx') $call.Arguments[-1] 'Solution path remains one argument.'
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/m:1' }).Count 'Node limit is forwarded.'
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/v:q' }).Count 'Verbosity is forwarded.'
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/p:OfficialBuild=true' }).Count 'Provenance mode is shared across phases.'
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/p:BUILD_SOURCEVERSION=fixture-commit' }).Count 'Source revision is shared across phases.'
        if ($call.Command -ne 'restore') {
            Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '--no-restore' }).Count 'Build and pack reuse restore.'
        }
        if ($call.Command -eq 'pack') {
            Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '--no-build' }).Count 'Pack reuses the build.'
        }
    }
}

try {
    [void] (New-Item -ItemType Directory -Path $temporaryRoot)
    foreach ($file in @('build.ps1', 'common.ps1', 'global.json')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $temporaryRoot
    }

    @'
param(
    [string] $Action = 'BuildAndPack',
    [switch] $NoRestore,
    [string] $VersionDateSuffix,
    [string] $DateStamp = '202610291859',
    [string] $FailedCommand
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Exercise the real orchestration and argument forwarding without installing an SDK or building the solution.
function dotnet.exe { }
function dotnet {
    if ($args[0] -eq '--list-sdks') {
        $version = (Get-Content .\global.json | ConvertFrom-Json).sdk.version
        return "$version [test SDK]"
    }

    [pscustomobject]@{
        Command = $args[0]
        Arguments = @($args | Select-Object -Skip 1)
    } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.jsonl')
    $global:LASTEXITCODE = if ($args[0] -eq $FailedCommand) { 23 } else { 0 }
}
function Get-Date { param([string] $Format) return $DateStamp }

$parameters = @{ Action = $Action; NoRestore = $NoRestore }
if ($VersionDateSuffix) {
    $parameters.VersionDateSuffix = $VersionDateSuffix
}
& .\build.ps1 @parameters
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath (Join-Path $temporaryRoot 'run.ps1')

    $env:BuildConfiguration = 'Debug'
    $env:BUILD_FLAGS = '/m:1 /v:q /p:OfficialBuild=true /p:BUILD_SOURCEVERSION=fixture-commit'
    $first = @(Invoke-BuildCase)
    Assert-Commands $first 'restore,build,pack'
    $second = @(Invoke-BuildCase -Arguments @('-DateStamp', '202610291900'))
    Assert-Equal (ConvertTo-Json -InputObject $first -Depth 5 -Compress) (ConvertTo-Json -InputObject $second -Depth 5 -Compress) 'DefaultDebugIsStableAcrossMinuteBoundary.'
    foreach ($call in $first) {
        Assert-Equal 0 @($call.Arguments | Where-Object { $_ -like '/p:VersionDateSuffix=*' }).Count 'DefaultDebugIsStableAcrossMinuteBoundary.'
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/p:Configuration=Debug' }).Count 'Debug configuration.'
    }
    Write-Output 'Passed DefaultDebugIsStableAcrossMinuteBoundary'
    $testsRun++

    foreach ($case in @(
        @{ Name = 'BuildOnlyRestoresOnce'; Arguments = @('-Action', 'Build'); Commands = 'restore,build' },
        @{ Name = 'BuildOnlyReusesRestore'; Arguments = @('-Action', 'Build', '-NoRestore'); Commands = 'build' },
        @{ Name = 'DefaultPackagingReusesRestore'; Arguments = @('-NoRestore'); Commands = 'build,pack' },
        @{ Name = 'PackOnlyReusesBuild'; Arguments = @('-Action', 'Pack'); Commands = 'pack' }
    )) {
        $calls = @(Invoke-BuildCase -Arguments $case.Arguments)
        Assert-Commands $calls $case.Commands
        Write-Output "Passed $($case.Name)"
        $testsRun++
    }

    $calls = @(Invoke-BuildCase -Arguments @('-VersionDateSuffix', '202610291900'))
    Assert-Commands $calls 'restore,build,pack'
    foreach ($call in $calls) {
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/p:VersionDateSuffix=202610291900' }).Count 'ExplicitVersionIsSharedAcrossPhases.'
    }
    $calls = @(Invoke-BuildCase -Arguments @('-Action', 'Pack', '-VersionDateSuffix', '202610291900'))
    Assert-Commands $calls 'pack'
    Assert-Equal 1 @($calls[0].Arguments | Where-Object { $_ -eq '/p:VersionDateSuffix=202610291900' }).Count 'PackOnlyPreservesExplicitVersion.'
    Write-Output 'Passed ExplicitVersionIsSharedAcrossPhases'
    $testsRun++

    $env:BuildConfiguration = 'Release'
    $calls = @(Invoke-BuildCase)
    Assert-Commands $calls 'restore,build,pack'
    foreach ($call in $calls) {
        Assert-Equal 1 @($call.Arguments | Where-Object { $_ -eq '/p:Configuration=Release' }).Count 'ReleaseConfigurationIsSharedAcrossPhases.'
        Assert-Equal 0 @($call.Arguments | Where-Object { $_ -like '/p:VersionDateSuffix=*' }).Count 'ReleaseVersionRemainsUnchanged.'
    }
    Write-Output 'Passed ReleaseConfigurationIsSharedAcrossPhases'
    $testsRun++

    foreach ($case in @(
        @{ Command = 'restore'; Expected = 'restore' },
        @{ Command = 'build'; Expected = 'restore,build' },
        @{ Command = 'pack'; Expected = 'restore,build,pack' }
    )) {
        $calls = @(Invoke-BuildCase -Arguments @('-FailedCommand', $case.Command) -ExpectedExitCode 23)
        Assert-Commands $calls $case.Expected
        Write-Output "Passed StopsAfterFailed$($case.Command)"
        $testsRun++
    }

    $calls = @(Invoke-BuildCase -Arguments @('-Action', 'Invalid') -ExpectedExitCode 1)
    Assert-Equal 0 $calls.Count 'InvalidActionStopsBeforeDotnet.'
    Write-Output 'Passed InvalidActionStopsBeforeDotnet'
    $testsRun++

    Write-Output "Passed $testsRun build-script cases."
} finally {
    $env:BuildConfiguration = $originalConfiguration
    $env:BUILD_FLAGS = $originalFlags
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }

}

exit 0
