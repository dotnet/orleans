#Monitor Orleans Silos
#requires -version 2.0
param([string]$deploymentConfigFile, [string]$networkInstance, [int]$samplesToLog, [int]$headerInterval, [switch]$repeatHeaderInFile)

$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1

if (($deploymentConfigFile -eq "/?") -or 
	($args[0] -eq "-?") -or
	($deploymentConfigFile -eq "/help") -or
	($args[0] -eq "-help") -or
	($deploymentConfigFile -eq "help") )
{
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`tUsage:`t.\MonitorOrleansSilos [deploymentConfigFile] [networkInstance] [samplesToLog] [headerInterval] [repeatHeaderInFile]"
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`tdeployementConfigFile::`t[Optional] The path to the deployment configuration file. "
	WriteHostSafe Green -text "`t`t`t`t`t(i.e. ""Deployment.xml"")  Use quotes if the path has a spaces." 
	WriteHostSafe Green -text "`t`t`t`t`tDefault is Deployment.xml. "
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`tnetworkInstance::`t[Optional] The network instance to use for the newtork process counters."
	WriteHostSafe Green -text "`t`t`t`t`tDefault is corp."
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`tsamplesToLog::`t`t[Optional] How many samples to take at roughly one minute intervals."
	WriteHostSafe Green -text "`t`t`t`t`tDefault is 480 samples (which should take more than 8 hours)."
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`theaderInterval::`t[Optional] The number of samples to write before repeating the header."
	WriteHostSafe Green -text "`t`t`t`t`tDefault is 10."
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`trepeatHeaderInFile::`t[Optional] if true, the header will be repeated in the consolidated data file."
	WriteHostSafe Green -text "`t`t`t`t`tDefault is false."
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos "
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos .\MSR-4MachineDeploy.xml"
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos .\MSR-4MachineDeploy.xml isatap.redmond.corp.microsoft.com"
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos .\MSR-4MachineDeploy.xml corp 60 "
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos .\MSR-4MachineDeploy.xml corp 60 10"
	WriteHostSafe Green -text "`tExample:`t.\MonitorOrleansSilos .\MSR-4MachineDeploy.xml corp 60 10 -repeatHeaderInFile"
	WriteHostSafe Green -text ""
	return
}

# Change the path to where we think it sould be (see http://huddledmasses.org/powershell-power-user-tips-current-directory/).
[Environment]::CurrentDirectory=(Get-Location -PSProvider FileSystem).ProviderPath

$configXml = Get-DeploymentConfiguration ([ref]$deploymentConfigFile) $scriptDir

# if we couldn't load the config file, the script cannot contiune.
if (!$configXml -or $configXml -eq "")
{
	WriteHostSafe -foregroundColor Red -text "     Deployment configuration file required to continue."
	WriteHostSafe -foregroundColor Red -text "          Please supply the name of the configuration file, or ensure that the default"
	WriteHostSafe -foregroundColor Red -text "          Deployment.xml file is available in the script directory."
	return
}

if ($configXml.Deployment.Program)
{
	$exeName = $configXml.Deployment.Program.ExeName
	if (!$exeName)
	{
		$exeName = "OrleansHost"
	}
}
Echo "Program executable = $exeName"

if (!$networkInstance)
{
	$networkInstance = "corp"
}

if (!$samplesToLog)
{
	# Defualt to 8 hours.
	$samplesToLog = 480;
}

if (!$headerInterval)
{
	$headerInterval = 10
}

# Gets the counter data for the given counter path on the given machine.
Function GetCounterData 
{param ([string]$machineName, [string]$counterPath, $getCooked)
	
	$fullCounterPath = "\\{0}\{1}" -f $machineName, $counterPath
#	Write-Host ("Machine: {0}`nPath: {1}`nBoth: {2}`n`n" -f $machineName, $counterPath, $fullCounterPath)
	$usage = Get-Counter -Counter $fullCounterPath -ErrorAction SilentlyContinue
	
	if ($usage)
		{
		if ($getCooked)
		{
			$result = $usage.CounterSamples[0].CookedValue
		}
		else 
		{
			$result = $usage.CounterSamples[0].RawValue
		}
	}
	else
	{
		$result = "No Data"
	}
	return $result
}

$machineNames = Get-UniqueMachineNames $configXml $deploymentConfigFile

if(!$machineNames)
{
	WriteHostSafe -foregroundColor Red -text "     At least one target machine is required to continue."
	WriteHostSafe -foregroundColor Red -text ""
	return
}

$logDate = (Get-Date -Format "yyyy-MM-dd-HH.mm.ss.ff")

$targetFolderName = "PerformanceData"
if(!(Test-Path $targetFolderName))
{
	New-Item -Path . -Name $targetFolderName -ItemType directory 
}

$machineProcessorCounts = @()

# Get the number of logical processors for each machine so we can calcluate the appropriate usage percentage.
foreach($machineName in $machineNames)
{
	$wmiData = gwmi win32_computersystem -ComputerName $machineName
	$machineProcessorCounts += $wmiData.NumberOfLogicalProcessors	
}

$perfDataConsolidatedFile = "{0}\ConsolidatedPerfData-{1}.log" -f $targetFolderName, $logDate
$consolidatedDataHeader = "`n{0,-7} {1,-25}  {2,-14}  {3,-12}  {4,-8}  {5,-17}  {6,-20}" -f "Sample", "TimeStamp", "Machine", "Memory(MB)", "CPU %", "Network Sent(MB)", "Network Received(MB)"

$bytesInAMegabyte = 1048576
for ($sampleCount = 0; $sampleCount -lt $samplesToLog; $sampleCount++) 
{
 	# Print out a header to the console.
	if ($sampleCount % $headerInterval -eq 0)
	{
		Write-Host -ForegroundColor Green $consolidatedDataHeader
		
		if ($repeatHeaderInFile -or $sampleCount -eq 0)
		{
			Add-Content $perfDataConsolidatedFile $consolidatedDataHeader
		}
	}
	$machineNumber = 0;
	foreach($machineName in $machineNames)
	{
		$memoryUsage = GetCounterData $machineName "Process($exeName)\Working Set"
		if($memoryUsage.GetType().Name -ne "String") 
		{
			$memoryUsage /= $bytesInAMegabyte
		}
		
		$cpuUsage = GetCounterData $machineName "Process($exeName)\% Processor Time" $true
		if(($cpuUsage.GetType().Name -ne "String") -and ($cpuUsage -ne 0))
		{
			$cpuUsage /= $machineProcessorCounts[$machineNumber]			
		}
		
		$networkBytesSent = GetCounterData $machineName "Network Interface($networkInstance)\Bytes Sent/sec" 
		if($networkBytesSent.GetType().Name -ne "String") 
		{
			# convert bytes to MB
			$networkBytesSent /= $bytesInAMegabyte
		}
		
		$networkBytesReceived = GetCounterData $machineName "Network Interface($networkInstance)\Bytes Received/sec" 
		if($networkBytesReceived.GetType().Name -ne "String") 
		{
			$networkBytesReceived /= $bytesInAMegabyte
		}
		$machineSpecificDataFile = "{0}\PerfData-{1}-{2}.log" -f $targetFolderName, $machineName, $logDate
		$timestamp = Get-Date -Format "MM/dd/yyyy HH:mm:ss:fff"
		$machineSpecificData = "{0}`t{1}`t{2}`t{3}`t{4}" -f $timestamp, $memoryUsage, $cpuUsage, $networkBytesSent, $networkBytesReceived 
		$verboseData = "{0,-7} {1,-25}  {2,-14}  {3,-12:N3}  {4,-8:N3}  {5,-17:N3}  {6,-20:N3}" -f $sampleCount, $timestamp, $machineName, $memoryUsage, $cpuUsage, $networkBytesSent, $networkBytesReceived
		Write-Host -ForegroundColor Cyan $verboseData
		Add-Content $machineSpecificDataFile $machineSpecificData
		Add-Content $perfDataConsolidatedFile $verboseData
		$machineNumber++
	}
	Start-Sleep -Seconds 60
	Write-Host 
	Add-Content $perfDataConsolidatedFile " "
}
