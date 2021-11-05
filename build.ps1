# --------------------
# Orleans build script
# --------------------

. ./common.ps1

$scriptDir = Split-Path $script:MyInvocation.MyCommand.Path
$solution = Join-Path $scriptDir "Orleans.sln"

# Define build flags & config
if ($null -eq $BUILD_FLAGS)
{
    $BUILD_FLAGS = "/m /v:m"
}
if ($null -eq $BuildConfiguration)
{
    $BuildConfiguration = "Debug"
}

# Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
# misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
$Platform = $null

# Disable multilevel lookup https://github.com/dotnet/core-setup/blob/master/Documentation/design-docs/multilevel-sharedfx-lookup.md
 $DOTNET_MULTILEVEL_LOOKUP = 0 

 # Set DateTime suffix for debug builds
 if ($BuildConfiguration -eq "Debug")
 {
    $dateSuffix = Get-Date -Format "yyyyMMddHHmm"
    $AdditionalConfigurationProperties=";VersionDateSuffix=$dateSuffix"
 }

Write-Output "===== Building $solution ====="

Install-Dotnet

if ($args[0] -ne "Pack")
{
    Write-Output "Build $BuildConfiguration =============================="
    Invoke-Dotnet -Command "restore" -Arguments "$BUILD_FLAGS /bl:${BuildConfiguration}-Restore.binlog /p:Configuration=${BuildConfiguration}${AdditionalConfigurationProperties} $solution"
    Invoke-Dotnet -Command "build" -Arguments "$BUILD_FLAGS /bl:${BuildConfiguration}-Build.binlog /p:Configuration=${BuildConfiguration}${AdditionalConfigurationProperties} $solution"
}

Write-Output "Package $BuildConfiguration ============================"
Invoke-Dotnet -Command "pack" -Arguments "--no-build --no-restore $BUILD_FLAGS /bl:${BuildConfiguration}-Pack.binlog /p:Configuration=${BuildConfiguration}${AdditionalConfigurationProperties} $solution"

Write-Output "===== Build succeeded for $solution ====="