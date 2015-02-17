# GlobalStop.ps1
# For use with the PerfTests package.  Stops all silos and clients, then copies specified files.

#requires -version 2.0

param($deploymentConfigFile, $deploymentSiloConfigFile)

$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1
. $scriptDir\TestUtilities.ps1


# Prep for copying.
# $localTestPath is constructed with the run ID (usually a timestamp) and the name of the test being run, if known.
if (!(Test-Path "$localTestPath"))
{
    [IO.Directory]::CreateDirectory(".\$localTestPath")
}


# Copy silo logs.
Invoke-Expression ".\GatherOrleansSiloLogs.ps1 $deploymentSiloConfigFile $localTestPath"


# Shut down all clients that could be running across all possible client machines.
# The TestUtilities.ps1 script has already loaded configuration data about different clients.
# So, given a list of clients and a list of executable names that could be in use,
# attempt to kill each one.
foreach ($clientMachine in $clientMachines)
{    
    foreach ($package in $packages)
    {
        foreach ($TaskToKill in $package.TasksToKill)
        {
            $killCommand = "powershell -command kill -name $TaskToKill"
            Invoke-WmiMethod -path win32_process -name create -argumentlist "$killCommand" -ComputerName $clientMachine.name
        }
    }
}


# Copy client files.  Make no assumptions about which client package was running.
# Attempt to copy all possible data from all possible client machines.
# The files to copy are filtered in ClientConfiguration.xml.
foreach ($clientMachine in $clientMachines)
{    
    foreach ($package in $packages)
    {
        $fromPath = ($clientMachine.hostPath + "\" + $package.TargetPath)
        # Test if this machine was running this package.  If so, collect the logs.
        if (Test-Path ($fromPath))
        {
            # Each client machine gets its own subfolder.
            $targetPath = ("$localTestPath\{0}" -f $clientMachine.name)
            if (!(Test-Path($targetPath)))
            {
                [IO.Directory]::CreateDirectory($targetPath)
            }
            
            # TODO: FIXME: Robocopy has a problem with multiple filters at once?
            <#
        	$filter = ""
            foreach ($fileFilter in $package.Filters)
            {
                $filter = $filter + " $fileFilter"
            }
            
            robocopy $fromPath $targetPath $filter
            #>

            foreach ($fileFilter in $package.Filters)
            {
                robocopy $fromPath $targetPath $fileFilter
            }
        }
    }
}

# Delete all client data from the client machines, including the executables themselves.
# This will provide a clean slate for the next deployment.
foreach ($clientMachine in $clientMachines)
{    
    foreach ($package in $packages)
    {
        $fromPath = ($clientMachine.hostPath + $package.TargetPath)
        if (Test-Path ($fromPath))
        {
            echo "Removing at $fromPath ..."
            rm -r $fromPath
        }
    }
}

# Shut down silos.  This also cleans out old data from the silos.
Invoke-Expression ".\UndeployOrleansSilos.ps1 $deploymentSiloConfigFile"

# Did this run abort with no files copied?  Delete the directory.
if ((Resolve-Path "$localTestPath" | Get-ChildItem).Count -gt 0)
{
    echo "Deletion successful.  Data stored in $localTestPath."
}
else
{
    echo "Deletion successful, nothing to log."
    rm -r (Resolve-Path "$localTestPath")
}