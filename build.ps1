# --------------------
# Orleans build script
# --------------------

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

 function Install-Dotnet 
 {
    $installDotnet = $true
    try 
    {
        # Check that the dotnet.exe command exists
        Get-Command dotnet.exe 2>&1 | Out-Null
        # Read the version from global.json
        $globalJson = Get-Content .\global.json | ConvertFrom-Json
        $requiredVersion = $globalJson.sdk.version
        # Check versions already installed
        $localVersions = dotnet --list-sdks |% {  $_.Split(" ")[0] }
        Write-Output "Required SDK: $requiredVersion"
        Write-Output "Installed SDKs: $localversions"
        # If the required version is not installed, we will call the installation script
        $installDotnet = !$localVersions.Contains($globalJson.sdk.version)
    }
    catch
    {
        Write-Output "dotnet not found"
        # do nothing, we will install dotnet
    }

    if ($installDotnet)
    {
        Write-Output "Installing dotnet ${requiredVersion}"

        New-Item -ItemType Directory -Force -Path .\Tools | Out-Null

        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest `
            -Uri "https://dot.net/v1/dotnet-install.ps1" `
            -OutFile ".\Tools\dotnet-install.ps1"

        & .\Tools\dotnet-install.ps1 -InstallDir Tools\dotnetcli\ -JSonFile .\global.json
    }
 }

function Invoke-Dotnet 
{
    param 
    (
        $Command,
        $Arguments
    )
    $cmdArgs = @()
    $cmdArgs = $cmdArgs + $Command
    $cmdArgs = $cmdArgs + ($Arguments -split "\s+")
    Write-Output "dotnet $cmdArgs"
    & dotnet $cmdArgs
    if ($LASTEXITCODE -ne 0)
    {
        Write-Error "===== Build FAILED -- $Command with error $LASTEXITCODE - CANNOT CONTINUE ====="
        Exit $LASTEXITCODE
    }
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