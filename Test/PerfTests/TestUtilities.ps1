# This file is responsible for reading config XML files.
# It is not intended for stand-alone use.

. $scriptDir\UtilityFunctions.ps1
$configXml = New-Object XML                   # configXml: The client's configuration file.
$configSiloXml = New-Object XML               # configSiloXml: The silo's configuration file.
$generatedSiloDeploymentXml = New-Object XML  # generatedSiloDeploymentXml: The generated deployment file for the silo.  Read the original and change it.

$generatedSiloDeploymentFilename = "GeneratedDeployment.xml"  # The filename to save the modified Deployment.xml file into



# Change the path to where we think it should be (see http://huddledmasses.org/powershell-power-user-tips-current-directory/).
[Environment]::CurrentDirectory=(Get-Location -PSProvider FileSystem).ProviderPath

if (!$deploymentConfigFile) 
{
	$deploymentConfigFile = "$scriptDir\ClientDeployment.xml"
	WriteHostSafe -foregroundColor Yellow -text ("     Defaulting to deployment client manifest location ""{0}""." -f $deploymentConfigFile)
	Echo ""
}
if (!$deploymentSiloConfigFile)
{
	$deploymentSiloConfigFile = "$scriptDir\Deployment.xml"
	WriteHostSafe -foregroundColor Yellow -text ("     Defaulting to deployment silo manifest location ""{0}""." -f $deploymentSiloConfigFile)
	Echo ""
}

if(![System.IO.File]::Exists($deploymentConfigFile))
{
	Echo ""
	WriteHostSafe -foregroundColor  Red -text "     Cannot find the Client Deployment configuration file at the given location: $deploymentConfigFile"
	Echo ""
	return	
}
if(![System.IO.File]::Exists($deploymentSiloConfigFile))
{
	Echo ""
	WriteHostSafe -foregroundColor  Red -text "     Cannot find the Silo Deployment configuration file at the given location: $deploymentSiloConfigFile"
	Echo ""
	return	
}

# Load the Deployment configuration file.
$configXml.Load("$deploymentConfigFile")
$configSiloXml.Load("$deploymentSiloConfigFile")

# Try to get the $localTargetPath from the target location node in the config file.
# This is the root where we will install all client programs on the client machines.
$localTargetPath = $configXml.Deployment.TargetLocation.Path

$OrleansConfigurationFile = $configXml.Deployment.RuntimeConfiguration.Path
$ClientConfigurationFile = $configXml.Deployment.ClientConfiguration.Path

if (!$OrleansConfigurationFile) { $OrleansConfigurationFile = "OrleansConfiguration.xml" }
if (!$ClientConfigurationFile) { $ClientConfigurationFile = "ClientConfiguration.xml" }
$GeneratedOrleansConfigurationFile = "GeneratedOrleansConfiguration.xml"
$GeneratedClientConfigurationFile = "GeneratedClientConfiguration.xml"

echo ("Orleans Config: $OrleansConfigurationFile    Client Config: $ClientConfigurationFile")


# Ensure that we have a good path, and that it's been created
if (!$localTargetPath)
{
	$localTargetPath = "C:\Orleans"
	WriteHostSafe -foregroundColor Yellow -text ("     TargetLocation not found in config file; defaulting to ""{0}""." -f $localTargetPath)
	Echo ""
}
if(!(test-path $localTargetPath -pathtype container))
{
	new-item $localTargetPath -type directory
}


# If target path is relative, convert it to absolute so it can be used by robocopy to remote machines.
$localTargetPath = (Resolve-Path $localTargetPath).Path

# Set the remote path by changing the drive designation to a remote admin share.
$remoteTargetPath = $localTargetPath.Replace(":", "$");

# Get the unique machine names.
$siloMachineNames = @($configSiloXml.Deployment.Nodes.Node | ForEach-Object {$_.HostName} | select-object -unique)
$clientMachineNames = @($configXml.Deployment.Clients.Client | ForEach-Object {$_.HostName} | select-object -unique)


# Get the path to the client files
$sourceXpath = "descendant::xcg:Package" 
$sourceConfig = $configXml.Deployment.Packages | Select-Xml -Namespace @{xcg="urn:xcg-deployment"} -XPath $sourceXpath
$tests = @($configXml.Deployment.Tests.Test)
$packages = @($sourceConfig | ForEach-Object {$_.Node} | select-object)

# Convert relative path to absolute so it can be passed to jobs.
$fullBaseCfgPath = Split-Path -Parent -Resolve "$deploymentConfigFile"


if ($clientMachineNames.Count -ne 1) {$pluralizer = "s"} else {$pluralizer = ""}
"Deploying to {0} machine{1}." -f $clientMachineNames.Count, $pluralizer

#Add a marker so that we see the seperation between the different deployments.
Add-Content "copyjob.log" ("*" * 107)
Add-Content "copyjob.log" ("*** Halt starting at {0}" -f (Get-Date))


$clientMachines = @()
foreach ($clientMachineName in $clientMachineNames) 
{
	#Add the machine name to the path.  We already convereted <Drive Letter>: to <Drive Letter>$
	#  (i.e. C: was changed to C$).
	if ($remoteTargetPath.StartsWith("\"))
	{
		$fullHostPath = "\\$clientMachineName{0}\" -f $remoteTargetPath
	}
	else 
	{
		$fullHostPath = "\\$clientMachineName\{0}\" -f $remoteTargetPath
	}
    # Set some properties that are valuable to other scripts.
	$clientMachine = "" | Select-Object name,processId,copyJob,hostPath;
	$clientMachine.name = $clientMachineName
	$clientMachine.hostPath = $fullHostPath
	$clientMachines = $clientMachines + $clientMachine
}

################################################################################
# Collect information on every client package specified in ClientDeployment.xml.
# This information will be accessible to other scripts.

$packages = @()

# Get a list of package paths.
$packageXpath = "descendant::xcg:Package" 
$packageConfig = $configXml.Deployment.Packages | Select-Xml -Namespace @{xcg="urn:xcg-deployment"} -XPath $packageXpath

Echo "Finding files..." 
foreach ($packageFromXml in $packageConfig)
{
    # Each package has a name, a path, a script to run to start the test, and a script to run post-test
    $package = "" | Select-Object Name, StartScript, PostScript, SourcePath, TargetPath, Filters, TasksToKill
    $package.Name = $packageFromXml.Node.Name
    $package.StartScript = $packageFromXml.Node.StartScript
    $package.PostScript = $packageFromXml.Node.PostScript
    $package.TargetPath = $packageFromXml.Node.TargetPath
    echo ("Found machine {0}" -f $package.Name)

    if (Split-Path $packageFromXml.Node.SourcePath  -IsAbsolute)
	{
		$package.SourcePath = $packageFromXml.Node.SourcePath
	}
	else 
	{
		$package.SourcePath = "{0}\{1}" -f $fullBaseCfgPath, $packageFromXml.Node.SourcePath
	}
    
    # Each package also specifies what files it wants copied back, and what task names it should kill off to clean up
    $package.Filters = @()
    $package.TasksToKill = @()
    foreach ($childNode in $packageFromXml.Node.ChildNodes)
    {
        if ($childNode.Name -eq "CopyFiles")
        {
            $package.Filters = $package.Filters + $childNode.FileFilter
            #echo ("Flagging files to copy: {0}.  There are now {1} filters in place" -f $childNode.FileFilter, $package.Filters.count)
        }
        if ($childNode.Name -eq "TaskToKill")
        {
            $package.TasksToKill = $package.TasksToKill + $childNode.Filename
            #echo ("Flagging task to kill: {0}.  There are now {1} tasks in place" -f $childNode.Filename, $package.TasksToKill.count)
        }
    }
    
    $packages = $packages + $package
}   


# Sometimes, we might not invoke a script like GlobalStop.ps1 as part of a full test run.
# If we're not doing this after a test run, it needs to go somewhere.
if (!$localTestPath) {

    $lastRunCount = 1
    $lastRunName = "Bucket"
    
    while (Test-Path ".\$lastRunName-$lastRunCount")
    {
        $lastRunCount = $lastRunCount + 1
    }
    
    $runid = "$lastRunName-$lastRunCount"
    $localTestPath = "$runid"
}

