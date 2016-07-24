#Script to clean off the Orleans files for a deployment.
#requires -version 2.0

param([string]$deploymentConfigFile, [string]$targetDirectory)

$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1

$configXml = New-Object XML

# Examlple 

if (($deploymentConfigFile -eq "/?") -or 
	($args[0] -eq "-?") -or
	($deploymentConfigFile -eq "/help") -or
	($args[0] -eq "-help") -or
	($deploymentConfigFile -eq "help") )
{
	echo ""
	echo "`tUsage:`t.\CleanOrleansSilos.ps1 [deployementConfigFile] [targetDirectory]"
	echo ""
	echo "`t`tdeployementConfigFile::`t[Optional] The path to the deployment configuration file. "
	echo "`t`t`t`t`t(i.e. ""Deployment.xml"")  Use quotes if the path has a spaces."
	echo "`t`t`t`t`tDefault is Deployment.xml. "
	echo ""
	echo "`t`ttargetDirectory::`t[Optional] the directory to be cleaned. (i.e. \Orleans)"
	echo "`t`t`t`t`tDefault is \Orleans."
	echo ""
	echo "`t`tExample:`t.\CleanOrleansSilos.ps1 "
	echo "`t`tExample:`t.\CleanOrleansSilos.ps1 ""\Program Files\Orleans"" "
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

#$machines = @($configXml.Deployment.Nodes.Node | ForEach-Object {$_.HostName} | select-object -unique)
$machines = Get-UniqueMachineNames $configXml $deploymentConfigFile

if(!$machines)
{
	# Error written by Get-UniqueMachineNames.
	WriteHostSafe -foregroundColor Red -text "     At least one target machine is required to continue."
	WriteHostSafe -foregroundColor Red -text ""	
	return
}

$targetDirectory  = $configXml.Deployment.TargetLocation.Path

if (!$targetDirectory)
{
	WriteHostSafe -foregroundColor "Red" -text "     Error: Target directory not found in configuration.  "
	WriteHostSafe -foregroundColor "Red" -text "            For saftey, a target must be provided on the command line or be in the Path attribute "
	WriteHostSafe -foregroundColor "Red" -text "            of the <TargetLocation> element in the deployment config file." 
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


"Removing Orleans from {0} machines" -f $machines.Count
foreach ($machine in $machines) 
{
	# TODO: Test to see if the machine is accessible.
	# Stop all instances of server process on the target computer.
	Echo "Stopping $exeName on $machine ..."
	StopOrleans $machine

	# TODO: Confirm that the loop expression can actually return a value.
	# TODO: Add a way to break out of the loop.
	#Wait until the processes shut down.
	while (IsProcessRunning $exeName $machine)
	{
		Echo "Waiting for Orleans to shut down..."
		Start-Sleep -Seconds 5
	}
	#TODO: Add code to abort if shutdown failed.
	
	#TODO: Make this more selective and provide options to leave things like log files and the app directory.
	# Remove the files.
	Echo "Cleaning Orleans directory."
	$resultMessage = "`t{0}" -f (CleanRemoteDirectory $targetDirectory "Orleans" $machine)
	
	Echo "Cleaning Cache directory."
	$cacheDirectory = "$Env:APPDATA\..\Local\OrleansData\FactoryCache"
	$resultMessage += "`t{0}" -f (CleanRemoteDirectory $cacheDirectory "Cache" $machine)
	
	"Result:`n{0}" -f $resultMessage
}