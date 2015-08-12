#Script to clean off the Orleans files for a deployment.
#requires -version 2.0

param([string]$deploymentConfigFile)

$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1

$configXml = New-Object XML

# Example 

if (($deploymentConfigFile -eq "/?") -or 
	($args[0] -eq "-?") -or
	($deploymentConfigFile -eq "/help") -or
	($args[0] -eq "-help") -or
	($deploymentConfigFile -eq "help") )
{
	echo ""
	echo "`tUsage:`t.\StopOrleansSilos.ps1 [deployementConfigFile]"
	echo ""
	echo "`t`tdeployementConfigFile::`t[Optional] The path to the deployment configuration file. "
	echo "`t`t`t`t`t(i.e. ""Deployment.xml"")  Use quotes if the path has a spaces."
	echo "`t`t`t`t`tDefault is Deployment.xml. "
	echo ""
	echo "`t`tExample:`t.\StopOrleansSilos.ps1 "
	echo "`t`tExample:`t.\StopOrleansSilos.ps1 ""C:\My data\Deployment.xml"" "
	echo ""
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

$machineNames = Get-UniqueMachineNames $configXml $deploymentConfigFile

if(!$machineNames)
{
	# Error written by Get-UniqueMachineNames.
	WriteHostSafe -foregroundColor Red -text "     At least one target machine is required to continue."
	WriteHostSafe -foregroundColor Red -text ""	
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

"Stopping $exeName on {0} machines" -f $machineNames.Count
foreach ($machineName in $machineNames) 
{
	# TODO: Test to see if the machine is accessible.
	# Stop all instances of OrleansHost down on the target computer.
	Echo "Stopping $exeName on $machineName ..."
	StopOrleans $machineName $exeName
}

foreach ($machineName in $machineNames) 
{
	# TODO: Add a way to break out of the loop.
	#Wait until the processes shut down.
	while (IsProcessRunning $exeName $machineName)
	{
		Echo "Waiting for $exeName to shut down on $machineName ..."
		Start-Sleep -Seconds 5
	}
	#TODO: Add code to abort if shutdown failed.
}

WriteHostSafe Green -text ""
WriteHostSafe Green -text "Checking for active processes"
$numProcesses = 0
foreach ($machineName in $machineNames) 
{
	$process = Get-Process -ComputerName $machineName -Name "$exeName" -ErrorAction SilentlyContinue
	if ($process)
	{
		WriteHostSafe -foregroundColor Red -text "$exeName is still running on $machineName :"
		if ($process.Count)
		{
			$processCount = 1
			foreach($instance in $process)
			{
				WriteHostSafe -foregroundColor Yellow -text ("  Process {0}: {1}" -f $processCount++, $instance.Id)
				$numProcesses++
			}
		}
		else
		{
			WriteHostSafe -foregroundColor Yellow -text ("  Process: {0}" -f $process.Id)
			$numProcesses++
		}
	}
}
WriteHostSafe Green -text ("{0} processes running" -f $numProcesses)
