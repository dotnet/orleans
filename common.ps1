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