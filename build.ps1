# --------------------
# Orleans build script
# --------------------

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("BuildAndPack", "Build", "Pack")]
    [string] $Action = "BuildAndPack",
    [switch] $NoRestore
)

. ./common.ps1

$scriptDir = Split-Path $script:MyInvocation.MyCommand.Path
$solution = Join-Path $scriptDir "Orleans.slnx"

# Define build flags & config
if ($null -eq $env:BUILD_FLAGS)
{
    $env:BUILD_FLAGS = "/m /v:m"
}
if ($null -eq $env:BuildConfiguration)
{
    $env:BuildConfiguration = "Debug"
}

# Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
# misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
$Platform = $null

# Disable multilevel lookup https://github.com/dotnet/core-setup/blob/main/Documentation/design-docs/multilevel-sharedfx-lookup.md
 $DOTNET_MULTILEVEL_LOOKUP = 0

$BuildArguments = @($env:BUILD_FLAGS -split "\s+" | Where-Object { $_ })
$BuildArguments += "/p:Configuration=$env:BuildConfiguration"

# Timestamp Debug packaging once so restore, build, and pack use the same version.
if ($env:BuildConfiguration -eq "Debug" -and $Action -ne "Build")
{
    $dateSuffix = Get-Date -Format "yyyyMMddHHmmssfff"
    $BuildArguments += "/p:VersionDateSuffix=$dateSuffix"
}

$BuildArguments += $solution

Write-Output "===== Building $solution ====="

Install-Dotnet

if ($Action -ne "Pack")
{
    Write-Output "Build $env:BuildConfiguration =============================="
    if (-not $NoRestore)
    {
        Invoke-Dotnet -Command "restore" -Arguments ($BuildArguments + "/bl:${env:BuildConfiguration}-Restore.binlog")
    }

    Invoke-Dotnet -Command "build" -Arguments (@("--no-restore") + $BuildArguments + "/bl:${env:BuildConfiguration}-Build.binlog")
}

if ($Action -ne "Build")
{
    Write-Output "Package $env:BuildConfiguration ============================"
    Invoke-Dotnet -Command "pack" -Arguments (@("--no-build", "--no-restore") + $BuildArguments + "/bl:${env:BuildConfiguration}-Pack.binlog")
}

Write-Output "===== Build succeeded for $solution ====="
