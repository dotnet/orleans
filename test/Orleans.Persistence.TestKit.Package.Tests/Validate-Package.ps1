[CmdletBinding()]
param(
    [string] $PackageVersion = '10.3.0-rc.1',
    [string] $Configuration = 'Release',
    [switch] $SkipExternalAssets
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sourceSolution = Join-Path $repositoryRoot 'Orleans.slnx'
$consumerProject = Join-Path $PSScriptRoot 'Orleans.Persistence.TestKit.Package.Tests.csproj'
$packageSource = Join-Path $repositoryRoot "Artifacts/PersistenceTestKit/$Configuration/packages"
$consumerPackagesPath = Join-Path $repositoryRoot "Artifacts/PersistenceTestKit/$Configuration/consumer-packages"
$buildExternalAssets = (-not $SkipExternalAssets).ToString().ToLowerInvariant()

if (Test-Path -LiteralPath $packageSource) {
    Remove-Item -LiteralPath $packageSource -Recurse -Force
}

if (Test-Path -LiteralPath $consumerPackagesPath) {
    Remove-Item -LiteralPath $consumerPackagesPath -Recurse -Force
}

& dotnet pack $sourceSolution `
    --configuration $Configuration `
    --output $packageSource `
    -p:Version=$PackageVersion `
    -p:BuildExternalAssets=$buildExternalAssets `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Orleans package build failed with exit code $LASTEXITCODE."
}

$packagePath = Join-Path $packageSource "Microsoft.Orleans.Persistence.TestKit.$PackageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "The package build did not produce '$packagePath'."
}

& dotnet test $consumerProject `
    --configuration $Configuration `
    --framework net10.0 `
    --filter 'FullyQualifiedName=Orleans.Persistence.TestKit.Package.Tests.PersistenceTestKitPackageConsumerTests.PersistenceStorage_WriteRead_StringKey' `
    -p:OrleansPackageVersion=$PackageVersion `
    -p:OrleansPackageSource=$packageSource `
    -p:RestorePackagesPath=$consumerPackagesPath `
    -p:RestoreForceEvaluate=true `
    --nologo `
    -- `
    -parallel none `
    -noshadow
if ($LASTEXITCODE -ne 0) {
    throw "Persistence test kit package smoke test failed with exit code $LASTEXITCODE."
}
