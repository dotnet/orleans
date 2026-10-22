[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoIncremental,
    [switch] $SkipExternalAssets,
    [switch] $DisableSharedCompilation
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSolution = Join-Path $repositoryRoot 'Orleans.slnx'
$sampleSolution = Join-Path $PSScriptRoot 'Samples.slnx'
$packageSource = Join-Path $repositoryRoot "Artifacts/Samples/$Configuration/packages"
$sampleBinlog = Join-Path $repositoryRoot "Artifacts/Samples/$Configuration/samples.binlog"
$packageVersion = "10.0.0-dev.$([System.DateTime]::UtcNow.Ticks)"
$buildExternalAssets = (-not $SkipExternalAssets).ToString().ToLowerInvariant()

if (Test-Path -LiteralPath $packageSource) {
    Remove-Item -LiteralPath $packageSource -Recurse -Force
}

& dotnet pack $sourceSolution `
    --configuration $Configuration `
    --output $packageSource `
    -p:Version=$packageVersion `
    -p:BuildExternalAssets=$buildExternalAssets `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Orleans package build failed with exit code $LASTEXITCODE."
}

$buildArguments = @(
    'build'
    $sampleSolution
    '--configuration'
    $Configuration
    "-p:OrleansSamplePackageVersion=$packageVersion"
    "-p:OrleansSamplePackageSource=$packageSource"
    '-p:RestoreForceEvaluate=true'
    "-bl:$sampleBinlog"
    '--nologo'
)
if ($NoIncremental) {
    $buildArguments += '--no-incremental'
}
if ($DisableSharedCompilation) {
    $buildArguments += '-p:UseSharedCompilation=false'
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Sample build failed with exit code $LASTEXITCODE."
}
