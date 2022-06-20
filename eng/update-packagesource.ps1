Param(
  [string] $barToken,
  [string] $gitHubPat,
  [string] $packagesSource
)

$ErrorActionPreference = "Stop"
. $PSScriptRoot\common\tools.ps1

# Batch and executable files exit and define $LASTEXITCODE.  Powershell commands exit and define $?
function CheckExitCode ([string]$stage, [bool]$commandExitCode = $True)
{
  $exitCode = 0
  if($commandExitCode -eq -$False) {
      $exitCode = 1
  }
  else {
    if ( Test-Path "LASTEXITCODE" -ErrorAction SilentlyContinue)
    {
      $exitCode = $LASTEXITCODE
    }
  }

  if ($exitCode -ne 0) {
    Write-PipelineTelemetryError -Category "UpdatePackageSource" -Message "Something failed in stage: '$stage'. Check for errors above. Exiting now with exit code $exitCode..."
    ExitWithExitCode $exitCode
  }
}

function StopDotnetIfRunning
{
    $dotnet = Get-Process "dotnet" -ErrorAction SilentlyContinue
    if ($dotnet) {
        stop-process $dotnet
    }
}

function AddSourceToNugetConfig([string]$nugetConfigPath, [string]$source) 
{
    Write-Host "Adding '$source' to '$nugetConfigPath'..."
    $nugetConfig = New-Object XML
    $nugetConfig.PreserveWhitespace = $true
    $nugetConfig.Load($nugetConfigPath)
    $packageSources = $nugetConfig.SelectSingleNode("//packageSources")
    $keyAttribute = $nugetConfig.CreateAttribute("key")
    $keyAttribute.Value = "arcade-local"
    $valueAttribute = $nugetConfig.CreateAttribute("value")
    $valueAttribute.Value = $source
    $newSource = $nugetConfig.CreateElement("add")
    $newSource.Attributes.Append($keyAttribute) | Out-Null
    $newSource.Attributes.Append($valueAttribute) | Out-Null
    $packageSources.AppendChild($newSource) | Out-Null
    $nugetConfig.Save($nugetConfigPath)
}

try {
  Push-Location $PSScriptRoot
  $nugetConfigPath = Join-Path $RepoRoot "NuGet.config"

  Write-Host "Adding local source to NuGet.config"
  AddSourceToNugetConfig $nugetConfigPath $packagesSource
  CheckExitCode "Adding source to NuGet.config" $?

  Write-Host "Updating dependencies using Darc..."
  $dotnetRoot = InitializeDotNetCli -install:$true
  $DarcExe = "$dotnetRoot\tools"
  Create-Directory $DarcExe
  $DarcExe = Resolve-Path $DarcExe
  . .\common\darc-init.ps1 -toolpath $DarcExe
  CheckExitCode "Running darc-init"

  $Env:dotnet_root = $dotnetRoot
  & $DarcExe\darc.exe update-dependencies --packages-folder $packagesSource --password $barToken --github-pat $gitHubPat --channel ".NET Tools - Latest"
  CheckExitCode "Updating dependencies"
}
catch {
  Write-Host $_.ScriptStackTrace
  Write-PipelineTelemetryError -Category "UpdatePackageSource" -Message $_
  ExitWithExitCode 1
}
finally {
  Write-Host "Cleaning up workspace..."
  StopDotnetIfRunning
  Pop-Location
}
ExitWithExitCode 0