[CmdletBinding()]
param(
    [string] $Version = $env:DOTNET_COVERAGE_VERSION,

    [string] $InstallPath
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

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'DOTNET_COVERAGE_VERSION must specify the dotnet-coverage tool version'
}

$toolPath = $InstallPath
if ([string]::IsNullOrWhiteSpace($toolPath)) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
        throw 'InstallPath must be specified outside GitHub Actions'
    }

    $toolPath = Join-Path $env:GITHUB_WORKSPACE '.tools'
}

Assert-NotReparsePoint $toolPath
dotnet tool install --tool-path $toolPath dotnet-coverage --version $Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet-coverage installation failed with exit code $LASTEXITCODE"
}

Assert-NotReparsePoint $toolPath
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_PATH)) {
    $toolPath >> $env:GITHUB_PATH
}
