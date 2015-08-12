# Shows if OrleansHost is running on the deployment machines.
#requires -version 2.0

param([string]$deploymentConfigFile)

$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1

$configXml = New-Object XML

if (($deploymentConfigFile -eq "/?") -or 
	($args[0] -eq "-?") -or
	($deploymentConfigFile -eq "/help") -or
	($args[0] -eq "-help") -or
	($deploymentConfigFile -eq "help") )
{
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`tUsage:`t.\ShowOrleansSilos [deploymentConfigFile] [noClean]"
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`tdeployementConfigFile::`t[Optional] The path to the deployment configuration file. "
	WriteHostSafe Green -text "`t`t`t`t`t(i.e. ""Deployment.xml"")  Use quotes if the path has a spaces." 
	WriteHostSafe Green -text "`t`t`t`t`tDefault is Deployment.xml. "
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`t`tnoClean::`t`t[Optional] If a value is provided, do not use robocopy /MIR option."
	WriteHostSafe Green -text "`t`t`t`t`tDefault is to mirror the source directory."
	WriteHostSafe Green -text ""
	WriteHostSafe Green -text "`tExample:`t.\ShowOrleansSilos "
	WriteHostSafe Green -text "`tExample:`t.\ShowOrleansSilos OrleansConfig1\config.config "
	WriteHostSafe Green -text "`tExample:`t.\ShowOrleansSilos OrleansConfig1\config.config X "
	WriteHostSafe Green -text ""
#	WriteHostSafe -foregroundColor  Red -text "Note: You must run this script from a PowerShell prompt that has Administrator privledges."
#	echo ""
	return
}

# Change the path to where we think it should be (see http://huddledmasses.org/powershell-power-user-tips-current-directory/).
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
	WriteHostSafe -foregroundColor Red -text "     At least one target machine is required to continue."
	WriteHostSafe -foregroundColor Red -text ""
	return
}

$numProcesses = 0
foreach ($machineName in $machineNames) 
{
	WriteHostSafe Green -text ""
	$process = Get-Process -ComputerName $machineName -Name "OrleansHost" -ErrorAction SilentlyContinue
	if (!$process)
	{
		WriteHostSafe -foregroundColor Red -text ("OrleansHost is not running on {0}." -f $machineName)
	}
	else
	{
		WriteHostSafe -foregroundColor Green -text ("OrleansHost running on {0}:" -f $machineName)
		if ($process.Count)
		{
			$processCount = 1
			foreach($instance in $process)
			{
				WriteHostSafe -foregroundColor Green -text ("     Process {0}: {1}" -f $processCount++, $instance.Id)
				$numProcesses++
			}
		}
		else
		{
			WriteHostSafe -foregroundColor Green -text ("     Process: {0}" -f $process.Id)
			$numProcesses++
		}
	}
}
WriteHostSafe Green -text ""
WriteHostSafe Green -text ("{0} processes running" -f $numProcesses)



