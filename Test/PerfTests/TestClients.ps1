# Deploys the silos defined in the OrleansRuntime.dll.config file.
#requires -version 2.0

param($deploymentConfigFile, $deploymentSiloConfigFile)

# Load script files.
$scriptDir = Split-Path -parent $MyInvocation.MyCommand.Definition
. $scriptDir\UtilityFunctions.ps1
. $scriptDir\TestUtilities.ps1

# The run ID is based on 
$runid = Get-Date -format "yy-MM-dd.HH.mm.ss"
WriteHostSafe Cyan -text "Run ID: $runid"

# Clean out old data.
# At this point, the $localTestPath variable has been set by $TestUtilities to "Bucket-#".
# Pre-existing data which may exist from a previous run will be copied to that folder.
. .\GlobalStop.ps1




# Get the path to the client files
$sourceXpath = "descendant::xcg:Package" 
$sourceConfig = $configXml.Deployment.Packages | Select-Xml -Namespace @{xcg="urn:xcg-deployment"} -XPath $sourceXpath
$tests = @($configXml.Deployment.Tests.Test)
$packages = @($sourceConfig | ForEach-Object {$_.Node} | select-object)

# Convert relative path to absolute so it can be passed to jobs.
$fullBaseCfgPath = Split-Path -Parent -Resolve "$deploymentConfigFile"


if ($clientMachineNames.Count -ne 1) {$pluralizer = "s"} else {$pluralizer = ""}
"Deploying to {0} machine{1}." -f $clientMachineNames.Count, $pluralizer



# These "Overrides" are options set in ClientDeployment.xml.
# Although the test is built on single ClientConfiguration.xml and OrleansConfiguration.xml files,
# the per-test "Overrides" will modify these files, allowing for different settings for each test.
# The "TestSettings" do not modify the configuration files, but are instead passed to
# the client's scripts.
$serverOverrides = @($configXml.Deployment.ServerOverrides.Override)
$clientOverrides = @($configXml.Deployment.ClientOverrides.Override)
$testSettings = @($configXml.Deployment.TestSettings.TestSetting)


# Detect duplicate test names.  Fail if any are found.
foreach ($testCompareFrom in $tests)
{
    $found = 0
    foreach ($testCompareTo in $tests)
    {
        if ($testCompareFrom.Name -eq $testCompareTo.Name)
        {
            $found = $found + 1
        }
    }

    if ($found -gt 1)
    {
        WriteHostSafe -foregroundColor Red -text ("Duplicate test name found: {0}. Aborting." -f $testCompareFrom.Name)
        return
    }
}    

# Run all tests.
foreach ($test in $tests)
{
    $testName = $test.Name

    # Each test specifies a Package, which is the client to use.  Look up the package, and find its start script and post-run script.
    $packageName = $test.Package
    $package = $packages | where-object -filter {$_.Name -eq $packageName}
    $testStartScript = $package.StartScript
    $testPostScript = $package.PostScript
    
    # Each test specifies a number of silos and clients to use, which may be less than the number of silos and clients provided.
    # If this is less than the full number, silos and clients will be chosen in the order that they appear.
    $siloCount = $test.Silos
    $clientCount = $test.Clients
    
    # $time is the time, in seconds, that this test should run for.  It is NOT enforced in TestClients.ps1.
    # Instead, it is passed to the start-script for each client, which must respect it.  If a test takes extra time
    # to set up, it may run for longer than specified in $time; this value only represents the time actually spent
    # executing the measured part of the test (for comparison purposes).
    $time = $test.Time
    
    WriteHostSafe -foregroundColor Cyan -text ("Starting copy for test $packageName.  Copying from {0} to {1}." -f $package.SourcePath, $package.TargetPath)

    # Deploy the client files to each machine.
    foreach ($clientMachine in $clientMachines)
    {    
        $originPath = $package.SourcePath
        $destPath = ($clientMachine.hostPath + "\" + $package.TargetPath)
        if (!(Test-Path($destPath)))
        {
            [IO.Directory]::CreateDirectory($destPath)
        }
        robocopy "$originPath" "$destPath" /S /XF *.temp /XF *.log /XD src /NDL /NFL
    }

    # The local path $localTestPath is where results will be collected.
    $localTestPath = "$runid\$testName"
    if (!(Test-Path ".\$localTestPath\"))
    {
        [IO.Directory]::CreateDirectory(".\$localTestPath")
    }
    
    # $testParameters is a collection of key-value pairs that will be passed to the test's start script.
    # For example, 'number of nodes in the grpah' is a test parameter.
    $testParameters = @{}
    
    # $descriptive is a string description of what modifiers were used, for human consumption in the logfiles.
    # It lists ServerOverrides, ClientOverrides, and TestSettings.
    $descriptive = ""
    


    WriteHostSafe -foregroundColor Cyan -text ("Running test: $testName, siloCount=$siloCount, clientCount=$clientCount, time=$time")


    #####################################
    # Generate our deployment config file
    # Read the original file, then save out a differently-named, generated version to use.
    
    # First, load the original file.
    $generatedSiloDeploymentXml.Load("$deploymentSiloConfigFile")
    
    # Read all the silos in the file.  Then, remove them all from our version.
    # We will re-add only the silos that we want to use.
    $silos = $generatedSiloDeploymentXml.Deployment.Nodes
    foreach ($childSilo in @($generatedSiloDeploymentXml.Deployment.Nodes.ChildNodes))
    {
        $generatedSiloDeploymentXml.Deployment.Nodes.RemoveChild($childSilo)
    }
    
    # Add silos up to the number that this test requires.
    foreach ($silo in $siloMachineNames[0 .. ($siloCount - 1)])
    {
        $childSilo = $generatedSiloDeploymentXml.CreateElement("Node", "urn:xcg-deployment")
        $hostnameAttribute = $generatedSiloDeploymentXml.CreateAttribute("HostName")
        $hostnameAttribute.value = $silo
        $nodenameAttribute = $generatedSiloDeploymentXml.CreateAttribute("NodeName")
        $nodenameAttribute.value = $silo
        $childSilo.Attributes.Append($hostnameAttribute)
        $childSilo.Attributes.Append($nodenameAttribute)
        $silos.AppendChild($childSilo)
    }
    
    # Save as the 'generated' version of the deployment file.
    # Silo deployment will use this version.
    $generatedSiloDeploymentXml.Save("$localTestPath\$generatedSiloDeploymentFilename")
    $generatedSiloDeploymentXml.Save("$generatedSiloDeploymentFilename")
    Invoke-Expression ("& '{0}\UndeployOrleansSilos.ps1' $generatedSiloDeploymentFilename" -f $scriptDir)
    start-sleep -seconds 2


    # Find the correct path to copy the client package from.    
	if (Split-Path $package.TargetPath -IsAbsolute)
	{
		$clientSourcePath = $package.TargetPath 
	}
	else 
	{
		$clientSourcePath = "{0}\{1}" -f $fullBaseCfgPath, $package.Path
	}
    
    
    #############################
    # Modify OrleansConfiguration
    # Read the original XML file, then apply ServerOverrides to it.
    # The saved-out generated copy will be used in this test.
    
    # Load the file
    $generatedOrleansConfigurationXml = New-Object XML
    $generatedOrleansConfigurationXml.Load("$OrleansConfigurationFile")
    $generatedOrleansConfigurationNSM = New-Object Xml.XmlNamespaceManager($generatedOrleansConfigurationXml.NameTable)
    $generatedOrleansConfigurationNSM.AddNamespace("namespace", $configXml.Deployment.ServerOverrides.DefaultNamespace)
    
    # For each override listed in this test...
    $serverOverridesRequested = @($test.ServerOverride)
    foreach ($serverOverrideRequested in $serverOverridesRequested)
    {
        # Search for the override in the list of server overrides.
        foreach ($serverOverride in $serverOverrides)
        {
            # Match found.  The override may contain multiple directives.
            if ($serverOverride.Name -eq $serverOverrideRequested.Name)
            {
                $descriptive = $descriptive + "(ServerOverride:" + $serverOverrideRequested.Name + ") "
                WriteHostSafe -foregroundcolor Cyan -text ("Applying ServerOverride ""{0}""..." -f $serverOverride.Name)
                foreach ($serverOverrideData in $serverOverride.ChildNodes)
                {
                    # Delete a line from the original XML (possibly to replace it with a similar, but different line)
                    if ($serverOverrideData.Name -eq "Delete")
                    {
                        WriteHostSafe -foregroundcolor Cyan -text ("    Deleting XPath ""{0}""..." -f $serverOverrideData.XPath)
                        $nodesToDelete = @($generatedOrleansConfigurationXml.SelectNodes($serverOverrideData.XPath, $generatedOrleansConfigurationNSM))
                        
                        foreach ($nodeToDelete in $nodesToDelete)
                        {
                            WriteHostSafe -foregroundcolor Cyan -text ("        Deleting entry...")
                            $nodeToDelete.ParentNode.RemoveChild($nodeToDelete) | Out-Null
                        }
                    }
                    if ($serverOverrideData.Name -eq "AddNode")
                    {
                        # Add a line to the new XML file
                        WriteHostSafe -foregroundcolor Cyan -text ("    Adding at XPath ""{0}""..." -f $serverOverrideData.XPath)
                        $newParentNode = $generatedOrleansConfigurationXml.SelectSingleNode($serverOverrideData.XPath, $generatedOrleansConfigurationNSM)
                        if ($newParentNode)
                        {
                            WriteHostSafe -foregroundcolor Cyan -text ("        Found, adding...")
                            foreach ($childNodeFrom in $serverOverrideData.ChildNodes)
                            {
                                $childNodeTo = $generatedOrleansConfigurationXml.CreateElement($childNodeFrom.Name, "urn:orleans")
                                foreach ($childNodeFromAttribute in $childNodeFrom.Attributes)
                                {
                                    $attributeTo = $generatedOrleansConfigurationXml.CreateAttribute($childNodeFromAttribute.Name)
                                    $attributeTo.Value = $childNodeFromAttribute.Value
                                    $childNodeTo.Attributes.Append($attributeTo) | Out-Null
                                }
                                $newParentNode.AppendChild($childNodeTo) | Out-Null
                            }
                        }
                    }
                }
            }
        }
    }
    
    # Save the generated version out to a temporary file.
    # This generated version is used when deploying.
    $generatedOrleansConfigurationXml.Save("$localTestPath\$GeneratedOrleansConfigurationFile")
    $generatedOrleansConfigurationXml.Save("$GeneratedOrleansConfigurationFile")
    
    
    
    #############################
    # Modify ClientConfiguration
    # Read the original XML file, then apply ClientOverrides to it.
    # Multiple copies will be generated, one for each client (see below).
    # Each one will be copied to the appropriate client machine before the test is run.
    
    # Load the file
    $generatedClientConfigurationXml = New-Object XML
    $generatedClientConfigurationXml.Load("$ClientConfigurationFile")
    $generatedClientConfigurationNSM = New-Object Xml.XmlNamespaceManager($generatedClientConfigurationXml.NameTable)
    $generatedClientConfigurationNSM.AddNamespace("namespace", $configXml.Deployment.ClientOverrides.DefaultNamespace)
    
    # For each override listed in this test...
    $ClientOverridesRequested = @($test.ClientOverride)
    foreach ($ClientOverrideRequested in $ClientOverridesRequested)
    {
        # Search for the override in the list of server overrides.
        foreach ($ClientOverride in $ClientOverrides)
        {
            # Match found.  The override may contain multiple directives.
            if ($ClientOverride.Name -eq $ClientOverrideRequested.Name)
            {
                $descriptive = $descriptive + "(ClientOverride:" + $clientOverrideRequested.Name + ") "
                WriteHostSafe -foregroundcolor Cyan -text ("Applying ClientOverride ""{0}""..." -f $ClientOverride.Name)
                foreach ($ClientOverrideData in $ClientOverride.ChildNodes)
                {
                    # Delete a line from the original XML (possibly to replace it with a similar, but different line)
                    if ($ClientOverrideData.Name -eq "Delete")
                    {
                        WriteHostSafe -foregroundcolor Cyan -text ("    Deleting XPath ""{0}""..." -f $ClientOverrideData.XPath)
                        $nodesToDelete = @($generatedClientConfigurationXml.SelectNodes($ClientOverrideData.XPath, $generatedClientConfigurationNSM))
                        
                        foreach ($nodeToDelete in $nodesToDelete)
                        {
                            WriteHostSafe -foregroundcolor Cyan -text ("        Deleting entry...")
                            $nodeToDelete.ParentNode.RemoveChild($nodeToDelete) | Out-Null
                        }
                    }
                    if ($ClientOverrideData.Name -eq "AddNode")
                    {
                        # Add a line to the new XML file
                        WriteHostSafe -foregroundcolor Cyan -text ("    Adding at XPath ""{0}""..." -f $ClientOverrideData.XPath)
                        $newParentNode = $generatedClientConfigurationXml.SelectSingleNode($ClientOverrideData.XPath, $generatedClientConfigurationNSM)
                        if ($newParentNode)
                        {
                            WriteHostSafe -foregroundcolor Cyan -text ("        Found, adding...")
                            foreach ($childNodeFrom in $ClientOverrideData.ChildNodes)
                            {
                                $childNodeTo = $generatedClientConfigurationXml.CreateElement($childNodeFrom.Name, "urn:Client")
                                foreach ($childNodeFromAttribute in $childNodeFrom.Attributes)
                                {
                                    $attributeTo = $generatedClientConfigurationXml.CreateAttribute($childNodeFromAttribute.Name)
                                    $attributeTo.Value = $childNodeFromAttribute.Value
                                    $childNodeTo.Attributes.Append($attributeTo) | Out-Null
                                }
                                $newParentNode.AppendChild($childNodeTo) | Out-Null
                            }
                        }
                    }
                }
            }
        }
    }
    # Save out the base version of the generated client configuration.
    $generatedClientConfigurationXml.Save("$localTestPath\$GeneratedClientConfigurationFile")
    
    
    # Modify a file for each client.  The purpose is to make each client connect to a different gateway.
    for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
    {
        $generatedClientConfigurationXml.ClientConfiguration.Gateway.Address = "{0}" -f $siloMachineNames[$clientIndex % $siloCount]
        $generatedClientConfigurationXml.Save("$localTestPath\$GeneratedClientConfigurationFile" + "-$clientindex")
    }    


    # Get the custom parameters for this test
    $testSettingsRequested = @($test.TestSetting)
    foreach ($testSettingRequested in $testSettingsRequested)
    {
        foreach ($testSetting in $testSettings)
        {
            if ($testSetting.TestSettingName -eq $testSettingRequested.Name)
            {
                $descriptive = $descriptive + "(TestSetting:" + $testSettingRequested.Name + ") "
                WriteHostSafe -foregroundcolor Cyan -text ("Applying TestSetting ""{0}""..." -f $testSetting.TestSettingName)
                foreach ($testSettingAttribute in $testSetting.Attributes)
                {
                    if ($testSettingAttribute.Name -ne "TestSettingName")
                    {
                        WriteHostSafe -foregroundcolor Cyan -text ("    Adding custom attribute ""{0}"" with value ""{1}""" -f $testSettingAttribute.Name, $testSettingAttribute.Value)
                        $testParameters.Add($testSettingAttribute.Name, $testSettingAttribute.Value)
                    }
                }
            }
        }
    }
    

    # Copy client configuration files
    for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
    {
        $thisGeneratedClientConfigurationFile = "$localTestPath\$GeneratedClientConfigurationFile-$clientIndex"
        $clientMachineName = $clientMachines[$clientIndex].Name
    	if ($remoteTargetPath.StartsWith("\"))
    	{
    		$packageDirectory = "\\$clientMachineName{0}\{1}" -f $remoteTargetPath, $package.TargetPath
    	}
    	else 
    	{
    		$packageDirectory = "\\$clientMachineName\{0}\{1}" -f $remoteTargetPath, $package.TargetPath
    	}
        
        WriteHostSafe -foregroundcolor Cyan -text ("Copying client configuration file ""{0}"" to ""{1}""..." -f $thisGeneratedClientConfigurationFile, "$packageDirectory\$clientConfigurationFile")
        Copy-Item "$thisGeneratedClientConfigurationFile" "$packageDirectory\$clientConfigurationFile"
    }



    # Clear the old log:  This will help us determine whether we've started up successfully.
    # If we delete the logfile at this stage, and then attempt to start up the silos and see that there is no log file,
    # then we know that the silos never ran.
    foreach ($silo in $siloMachineNames[0 .. ($siloCount - 1)])
    {
        $logFilename = "OrleanRuntimeEventLog.txt"
        $logPath = $generatedSiloDeploymentXml.Deployment.TargetLocation.Path
        $remoteLogPath = $logPath.Replace(":", "$");

        if (Test-Path -Path "\\$silo\$remoteLogPath\$logFilename")
        {
            WriteHostSafe -foregroundcolor Yellow -text ("Logfile exists on $silo, deleting...")

            Remove-Item "\\$silo\$remoteLogPath\$logFilename"

            if (Test-Path -Path "\\$silo\$remoteLogPath\$logFilename")
            {
        	   WriteHostSafe -foregroundcolor Red -text ("Failed to delete logfile on $silo")
            }
            else
            {
        	   WriteHostSafe -foregroundcolor Green -text ("Successfully deleted logfile on $silo")
            }
        }
        else
        {
    	   WriteHostSafe -foregroundcolor Green -text ("Logfile did not exist on $silo")
        }
    }
    
    
    # Start silos.  Provide the filename of the generated deployment file.    
    Invoke-Expression ("& '{0}\DeployOrleansSilos.ps1' $generatedSiloDeploymentFilename" -f $scriptDir)
    start-sleep -seconds 10

    # Make sure silos have started
    $failed = 0
    $logFilename = "OrleanRuntimeEventLog.txt"
    $logPath = $generatedSiloDeploymentXml.Deployment.TargetLocation.Path
    
    foreach ($siloIndex in 0 .. ($siloCount - 1))
    {
        $silo = $siloMachineNames[$siloIndex]
    
        $remoteLogPath = $logPath.Replace(":", "$");

        if (Test-Path -Path "\\$silo\$remoteLogPath\$logFilename")
        {
    	   WriteHostSafe -foregroundcolor Green -text ("Logfile exists on $silo.  Appears to have started correctly")
        }
        else
        {
    	    WriteHostSafe -foregroundcolor Red -text ("Logfile does not exist on $silo.  Appears not to have started.  Skipping test.")
            Add-Content ".\$runid\summary.txt" ("ERROR : Silo $silo did not start.")
            $failed = 1
        }
    }

    if ($failed)
    {
        Add-Content ".\$runid\summary.txt" ("Skipping a test due to silo start failure..")
    }
    else
    {
        WriteHostSafe -foregroundcolor Green -text ("Invoking client in: $clientSourcePath")
        . $testStartScript
        . .\GlobalStop.ps1
        . $testPostScript
    }
}

Echo ""
echo 'End'


