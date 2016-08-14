[CmdletBinding()]
Param(
    [Orleans.Runtime.Configuration.ClientConfiguration]$clientConfig
)

Import-Module ..\OrleansPSUtils.dll

Add-Type -Path ..\Orleans.dll

Stop-GrainClient

Write-Output([Orleans.GrainClient]::IsInitialized)

Start-GrainClient -Config $clientConfig

Write-Output([Orleans.GrainClient]::IsInitialized)

$grainId = 1
$grainType = [Orleans.Runtime.IManagementGrain]

$grain = Get-Grain -GrainType $grainType -LongKey $grainId
Write-Output($grain)

$activeSilos = $grain.GetHosts($true).Result

Write-Output($activeSilos)

Stop-GrainClient

Write-Output([Orleans.GrainClient]::IsInitialized)