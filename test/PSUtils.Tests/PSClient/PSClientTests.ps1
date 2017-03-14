[CmdletBinding()]
Param(
    [Orleans.Runtime.Configuration.ClientConfiguration]$clientConfig
)

Import-Module ..\OrleansPSUtils.dll

Add-Type -Path ..\Orleans.dll

$didThrow = $false;
try {
  Stop-GrainClient;
} catch {
	$didThrow = $true;
}

Write-Output($didThrow);

$client = Start-GrainClient -Config $clientConfig

Write-Output($client -ne $null)

$grainId = 1
$grainType = [Orleans.Runtime.IManagementGrain]

$grain = Get-Grain -GrainType $grainType -LongKey $grainId -Client $client
Write-Output($grain)

$activeSilos = $grain.GetHosts($true).Result

Write-Output($activeSilos)

Stop-GrainClient -Client $client

Write-Output($client.IsInitialized)