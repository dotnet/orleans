[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoIncremental,
    [switch] $SkipExternalAssets
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSolution = Join-Path $repositoryRoot 'Orleans.slnx'
$sampleSolution = Join-Path $PSScriptRoot 'Samples.slnx'
$packageSource = Join-Path $repositoryRoot "Artifacts/Samples/$Configuration/packages"
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

$unpublishedSampleDependencies = @(
    (Join-Path $repositoryRoot 'src/Orleans.DurableTasks.Abstractions/Orleans.DurableTasks.Abstractions.csproj')
    (Join-Path $repositoryRoot 'src/Microsoft.Orleans.DurableTasks/Microsoft.Orleans.DurableTasks.csproj')
)
foreach ($project in $unpublishedSampleDependencies) {
    & dotnet pack $project `
        --configuration $Configuration `
        --output $packageSource `
        -p:Version=$packageVersion `
        -p:IsPackable=true `
        '-p:PackageReadmeFile=' `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Unpublished sample dependency package build failed with exit code $LASTEXITCODE."
    }
}

$buildArguments = @(
    'build'
    $sampleSolution
    '--configuration'
    $Configuration
    "-p:OrleansSamplePackageVersion=$packageVersion"
    "-p:OrleansSamplePackageSource=$packageSource"
    '-p:RestoreForceEvaluate=true'
    '--nologo'
)
if ($NoIncremental) {
    $buildArguments += '--no-incremental'
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Sample build failed with exit code $LASTEXITCODE."
}
