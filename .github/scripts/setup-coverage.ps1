[CmdletBinding()]
param(
    [string] $Version = $env:DOTNET_COVERAGE_VERSION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'DOTNET_COVERAGE_VERSION must specify the dotnet-coverage tool version'
}

$toolPath = Join-Path $env:GITHUB_WORKSPACE '.tools'
dotnet tool install --tool-path $toolPath dotnet-coverage --version $Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet-coverage installation failed with exit code $LASTEXITCODE"
}

$toolPath >> $env:GITHUB_PATH
