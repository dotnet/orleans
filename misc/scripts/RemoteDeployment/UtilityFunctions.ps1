# Utility functions used by various Orleans Deployment scripts.
#requires -version 2.0

# Function to determine if the current session has administrator privledges.
Function IsUserAdministrator
{
	$user = [System.Security.Principal.WindowsIdentity]::GetCurrent()             
	$userPrincipal = new-object System.Security.Principal.WindowsPrincipal($user)              
	$isAdmin = [System.Security.Principal.WindowsBuiltInRole]::Administrator            
	return $userPrincipal.IsInRole($isAdmin)
}


# Function to change logfile name in a configuration file. 
Function ChangeLogfile
{param($configfile, $filename)
   $ip
	$xml =New-Object XML
	$xml.Load($configfile)
    $entry = $xml.configuration.'system.diagnostics'.trace.listeners.add | where-object { $_.name -eq 'configFileListener' }
	if  ($entry.HasAttributes -eq "True")
	{
	$entry.initializeData = $filename
	}
	$xml.Save($configfile)
}

# Function to modify the OrleansConfiguration.xml with values from the OrleansDeploy.list file.
Function UpdateConfiguration
{param ($sourcePath, $silos, $maxSeedNodes) 
	# If the number of seed nodes is not valid, default to 2.
	if ((!$maxSeedNodes) -or ($maxSeedNodes -lt	0))
	{
		$maxSeedNodes = 2
	}
	
	# If the number of seed nodes exceeds the number of silos being installed, make them all seed nodes.
	if ($maxSeedNodes > $silos.Count)
	{
		$maxSeedNodes = $silos.Count
	}
	
	# Load the config document.
	[Environment]::CurrentDirectory=(Get-Location -PSProvider FileSystem).ProviderPath
	$orleansConfigDoc = New-Object XML
	$orleansConfigDoc.Load("$sourcePath\OrleansConfiguration.xml")

	#Create an XmlNamespaceManager to resolve the default namespace.
	$ns = New-Object Xml.XmlNamespaceManager $orleansConfigDoc.NameTable
	$ns.AddNamespace( "o", "urn:orleans" )
		
	# Get the <Deployment> element.
	$deploymentElement = $orleansConfigDoc.DocumentElement.SelectSingleNode("descendant::o:Deployment", $ns)
	$deploymentElement.OuterXml
	# Remove default Deployment elements.
	$deploymentElement.RemoveAll()
	
	# Get the <Globals> element.
	$globalsElement = $orleansConfigDoc.DocumentElement.SelectSingleNode("o:Globals", $ns)
	# Remove the default SeedNode elements.
	$seedElements = $globalsElement.SelectNodes("o:SeedNode", $ns)
	$seedElements | ForEach-Object { $globalsElement.RemoveChild($_) }
	
	$seedCount = 0
	Echo "Inside UpdateConfiguration: Begin Loop."

	foreach ($silo in $silos) 
	{
		# Build the new silo element.
		"Silo: {0} - {1}" -f $silo.SiloName, $silo.MachineName
		#$siloElement = BuildSiloElement $orleansConfigDoc $silo 
		$siloElement = $orleansConfigDoc.CreateElement("Silo", "urn:orleans")
		$siloNameAttribute = $orleansConfigDoc.CreateAttribute("Name");
		$siloNameAttribute.Value = $silo.SiloName
		$siloMachineAttribute = $orleansConfigDoc.CreateAttribute("HostName")
		$siloMachineAttribute.Value = $silo.MachineName
		$siloElement.Attributes.Append($siloNameAttribute)
		$siloElement.Attributes.Append($siloMachineAttribute)

		$deploymentElement.AppendChild($siloElement)

		# If we haven't assigned all the seed nodes, add this one.
		if ($seedCount -lt $maxSeedNodes)
		{
#			$seedNodeElement = BuildSeedNodeElement $orleansConfigDoc  $silo 
			# Create a new <SeedNode> element from the silo object.
			$seedNodeElement = $orleansConfigDoc.CreateElement("SeedNode", "urn:orleans")
			$seedNodeAddressAttribute = $orleansConfigDoc.CreateAttribute("Address")
			$seedNodeAddressAttribute.Value = $silo.MachineName
			$seedNodePortAttribute = $orleansConfigDoc.CreateAttribute("Port")
			# TODO: Need to get the port from somewhere.
			$seedNodePortAttribute.Value = 10000
			$seedNodeElement.Attributes.Append($seedNodeAddressAttribute)
			$seedNodeElement.Attributes.Append($seedNodePortAttribute)

			$globalsElement.AppendChild($seedNodeElement)
			$seedCount++
		}
	}	
	"Writing revised config file to {0}" -f "$sourcePath\OrleansConfiguration.xml"
	$orleansConfigDoc.Save("$sourcePath\OrleansConfiguration.xml")
}

Function Test
{param ([xml]$configDoc)
	$configDoc.FirstChild.OuterXml
}

Function Test2 
{param ($silo)
	$silo
}

Function Test3 
{param ([xml]$configDoc, $silo)
	echo "Test 3"
	$silo
	$configDoc.FirstChild.OuterXml
}

# Helper function to build a silo element.
Function BuildSiloElement
{param ([xml]$configDoc, $silo)
	$siloElement = $configDoc.CreateElement("Silo")
	$siloNameAttribute = $configDoc.CreateAttribute("Name", $silo.SiloName);
	$siloMachineAttribute = $configDoc.CreateAttribute("HostName", $silo.MachineName)
	$siloElement.Attributes.Append($siloNameAttribute)
	$siloElement.Attributes.Append($siloMachineAttribute)
	$siloElement.OuterXml
	return $siloElement
}

# Helper function to build a SeedNode.
Function BuildSeedNodeElement
{param ([xml]$configDoc, $silo) 
	
	# Create a new <SeedNode> element from the silo object.
	$seedNodeElement = $configDoc.CreateElement("SeedNode")
	$seedNodeAddressAttribute = $configDoc.CreateAttribute("Address", $silo.MachineName);
	# TODO: Need to get the port from somewhere.
	$seedNodePortAttribute = $configDoc.CreateAttribute("Port", "10000")
	$seedNodeElement.Attributes.Append($seedNodeAddressAttribute)
	$seedNodeElement.Attributes.Append($seedNodePortAttribute)

	return $seedNodeElement
}

# Function to remove all the files from the specified directory on a given machine and report the results.
# TODO: Consider adding filtering capablities.  Currently not necessary, but could come in handy later.
Function CleanRemoteDirectory
{param ($targetDirectory, $targetDirectoryLabel = "", $machine = "localhost", $myWhatIf=$false)
	if ($targetDirectory.IndexOf(':') -ge 0)
	{
		$pathOnly = $targetDirectory.Substring($targetDirectory.IndexOf(':')+1)
	}
	else
	{
		$pathOnly = $targetDirectory
	}
		$uncPath = '\\{0}\C${1}' -f $machine, $pathOnly
	if(Test-Path $uncPath)
	{
		#Take a shot at confirming that the directory we are about to delete is an Orleans folder.
		if ((Test-Path ("{0}\OrleansHost.exe" -f $uncPath)) -or
			(Test-Path ("{0}\Orleans.dll" -f $uncPath)) 		
			)
		{		
			$filesBefore = (Get-ChildItem $uncPath -Recurse).Count
			Get-ChildItem $uncPath | foreach ($_) {remove-item $_.fullname -recurse -Force -WhatIf:$myWhatIf}
			$filesAfter = (Get-ChildItem $uncPath -Recurse).Count
			$filesRemovedCount = ($filesBefore - $filesAfter)
			$remoteResultMessage = ("{0}`tFiles Removed = {1};`n" -f $uncPath, $filesRemovedCount.ToString())
		}
		else
		{
			$remoteResultMessage = ("Files not removed because the directory was not confirmed to contain Orleans system components.`n")
		}
	}
	else 
	{
		$remoteResultMessage = ("{0} `tdirectory ""{1}"" not found - no files removed;`n" -f $targetDirectoryLabel, $uncPath)
	}
	
	return $remoteResultMessage 
}

# Wrapper function for Write-Host so that the scripts can proceed in the unit test environment.
# (For details, see http://blogs.msdn.com/b/mwories/archive/2009/09/30/the-use-of-write-host-and-sql-server-agent-powershell-job-steps.aspx)
Function WriteHostSafe
{param ($foregroundColor = "Gray", $text, $noNewLine = $false)
	if ($host.Name -eq "ConsoleHost")
	{
		Write-Host -ForegroundColor:"$foregroundColor" -NoNewline:$noNewLine $text
	}
	else
	{
		Echo $text
	}
}

# Stops all instances of Orleans on each of the machines in the semicolon delimited list passed to it.
# If machineList is not provided it defaults to the local machine.
Function StopOrleans
{param ($machineList, $exeName)
	if (!$machineList)
	{
		$machineList = $Env:COMPUTERNAME
	}
	if (!$exeName)
	{
		$exeName = "OrleansHost"
	}

	# TODO: Need to handle case where some machines are running and some are not.
	
	$command = "Get-WmiObject Win32_Process -ComputerName $machineList -filter ""name='$exeName'""" 

	# TODO: Need to trap errors that occur when params are wrong such as invalid machine names.
	$processes = Invoke-Expression $command
	if ($processes -ne $null)
	{
		$processes | ForEach-Object {
			$killResult = $_.Terminate()
			switch ($killResult.ReturnValue) 
			{
				0 {$resultMessage = "Success"}
				2 {$resultMessage = "Access Denied"}
				3 {$resultMessage = "Insufficient Privilege"}
				8 {$resultMessage = "Unknown failure"}
				9 {$resultMessage = "Path not found"}
				21 {$resultMessage = "Invalid Parameter"}
			}

			"`tMachine: {0} `tProcessId: {1} `tResult: {2}" -f $_.__SERVER, $_.ProcessId, $resultMessage 
		}
	}
	else 
	{
		WriteHostSafe Yellow -text ("`t$exeName is not running on deployment machine(s):`n`r`t`t`t {0}" -f $machineList.ToString().Replace(",", "`n`r`t`t`t"))
	}
}
# Determines if the given process is running, and returns the process id(s) if it does.
# If the process is running more than once, it will return multiple process ids in a semicolon delimited list.
Function IsProcessRunning
{param ($processName, $targetComputer)

	$p = Get-Process $processName -ComputerName $targetComputer -ErrorAction SilentlyContinue
	
	if ($p) 
	{
		if($p.Count) 
		{
			foreach ($process in $p)
			{
				$pList = "{0}{1}{2}" -f $pList, $seperator, $process.Id
				$seperator = ";"
			}
		}
		else 
		{
			$pList = $p.Id
		}
		return $pList
	}
}

Function Start-EventMonitoring
{Param ($targetComputer)     
	
	$stopProcessQuery = "SELECT * FROM Win32_ProcessStopTrace"   
	$startProcessQuery = "SELECT * FROM Win32_ProcessStartTrace"   

	$startSourceIdentifier = "{0}:OrleansHostStart" -f $targetComputer
	$stopSourceIdentifier = "{0}:OrleansHostStop" -f $targetComputer
		
	if (!$targetComputer)
	{
		$targetComputer = $Env:COMPUTERNAME
	}
	
	# TODO: Figure out a way to filter on process name without hard coding.
	$startAction = {
		$e = $Event.SourceArgs[1].NewEvent
		if($e.ProcessName -match "OrleansHost")
		{
			$Message = 'ProcessStarted [{4}]: (Name = "{3}", ID = {0,5}, Parent = {1,5}, Time = {2,20})' -f $e.ProcessId, $e.ParentProcessId, $event.TimeGenerated, $e.ProcessName, $Event.ComputerName 
			Write-Host -ForegroundColor Green $Message   
		}
	}    
	
	# TODO: Figure out how to combine this with the $startAction; the only difference is the beginning of the message.
	$stopAction = {
		$e = $Event.SourceArgs[1].NewEvent
		if($e.ProcessName -match "OrleansHost")
		{
			$Message = 'ProcessStopped [{4}]: (Name = "{3}", ID = {0,5}, Parent = {1,5}, Time = {2,20})' -f $e.ProcessId, $e.ParentProcessId, $event.TimeGenerated, $e.ProcessName, $Event.ComputerName 
			Write-Host -ForegroundColor Red $Message  
		}
	}    

$previousSession = Get-PSSession -computername $targetComputer -ErrorAction SilentlyContinue
if ($previousSession)
{
	Remove-PSSession -Session $previousSession 
}
Unregister-Event  $stopSourceIdentifier -ErrorAction SilentlyContinue
Unregister-Event $startSourceIdentifier -ErrorAction SilentlyContinue

$session = New-PsSession $targetComputer
## Register for an event that fires when a service stops on the remote computer
## Set this to forward to the client machine.
Invoke-Command $session -ArgumentList $startProcessQuery, $startSourceIdentifier, $stopProcessQuery, $stopSourceIdentifier {
	param($startProcessQuery, $startSourceIdentifier, $stopProcessQuery, $stopSourceIdentifier)
    ## Register for the WMI event
	Register-WmiEvent -Query $startProcessQuery -SourceIdentifier $startSourceIdentifier -Forward
    Register-WmiEvent -Query $stopProcessQuery -SourceIdentifier $stopSourceIdentifier -Forward
}

# Register local events to receive the forwarding.
$null = Register-EngineEvent -SourceIdentifier $startSourceIdentifier -Action $startAction
$null = Register-EngineEvent -SourceIdentifier $stopSourceIdentifier -Action $stopAction

}

Function Stop-EventMonitoring
{Param ($targetComputer)

	# TODO: Work out a way to only unregister specific events.
	
#	$startSourceIdentifier = "{0}:OrleansHostStart" -f $targetComputer
#	$stopSourceIdentifier = "{0}:OrleansHostStop" -f $targetComputer
#
#	Invoke-Command -ComputerName $targetComputer -ArgumentList $startSourceIdentifier -ScriptBlock { param ($startSourceIdentifier) Get-EventSubscriber | Unregister-Event -SourceIdentifier $startSourceIdentifier -Force }
#	Invoke-Command -ComputerName $targetComputer -ArgumentList $stopSourceIdentifier -ScriptBlock { param ($stopSourceIdentifier) Get-EventSubscriber | Unregister-Event -SourceIdentifier $stopSourceIdentifier -Force }
	$session = Get-PSSession -computername $targetComputer
	Remove-PSSession -Session $session
	"Monitoring Stopped; subscribers unregistered on {0}."  -f $targetComputer
}

# Load the deployment configuration if possible.
Function Get-DeploymentConfiguration([ref]$deploymentConfigFile, $scriptDir)
{ 
	$configXml = New-Object XML
	$configFile = $deploymentConfigFile.Value

	if (!$configFile -or ($configFile.Length -le 5) )
	{
		$configFile = "$scriptDir\Deployment.xml"
		$deploymentConfigFile.Value = $configFile
		WriteHostSafe -foregroundColor  "Yellow" -text "     No value provided for parameter deployementConfigFile; using default:`n`r`t`t $configFile"
		WriteHostSafe -text " "
	}

	if(!(Test-Path $configFile))
	{
		# TODO: If we can't find the file by itself, then try looking in the $sourcePath.
		WriteHostSafe -text ""
		WriteHostSafe -foregroundColor Red -text ("     Cannot find the Deployment configuration file at the given location:`n`r`t`t $configFile")
		WriteHostSafe -text ""
		return $false
	}
	
	# Load the Deployment configuration file.
	$Error.Clear()
	try
	{
		$configXml.Load($configFile)
	}
	catch
	{
		WriteHostSafe -foregroundColor Red -text "     Cannot load the Deployment configuration file at the given location:`n`r`t`t $configFile"
		WriteHostSafe -foregroundColor Red -text "     Error Message: $Error"
		WriteHostSafe -text ""

		$configXml = $false
	}
	
	return $configXml
}

Function Get-UniqueMachineNames ([Xml]$configXml, [string]$deploymentConfigFile)
{
	$machineNames = $null
	$machineNameError = $false
	if ($deploymentConfigFile)
	{
		$configFileName = Split-Path -Path $deploymentConfigFile -Leaf
		$errorMessage = ("      *** Error: Configuration file ""$configFileName"" does not contain a <{0}> element.")
	}
	else
	{
		$errorMessage = "      *** Error: Configuration file does not contain a ""{0}"" element."
	}
	
	if($configXml.Deployment)
	{
		$nodesElement = $configXml.Deployment.Nodes
		
		if($nodesElement)
		{
			if ($nodesElement.Node)
			{
				# TODO: Validate that there is a HostName attribute.
				$machineNames = @($nodesElement.Node | ForEach-Object {$_.HostName} | select-object -unique)
			}
			else
			{
				$machineNameError = $true
				$badNodeName = "Node"
				$errorDetail = "          Please supply at least one element that defines a remote machine to access."
			}
		}
		else 
		{
			$machineNameError = $true
			$badNodeName = "Nodes"
			$errorDetail = "          Please supply the elements that define the remote machines to access."
		}
	}
	else 
	{
		$machineNameError = $true
		$badNodeName = "Deployment"
		$errorDetail = "          Please supply a valid deployment configuration file."
	}
	
	if ($machineNameError) 
	{
		WriteHostSafe -foregroundColor Red -text ($errorMessage -f $badNodeName)
		WriteHostSafe -foregroundColor Red -text $errorDetail
		WriteHostSafe -foregroundColor Red -text "          Format: <Deployment>"
		WriteHostSafe -foregroundColor Red -text "                      <Nodes>"
		WriteHostSafe -foregroundColor Red -text "                           <Node HostName=""RemoteComputerName"" NodeName=""SiloName"" />"
		WriteHostSafe -foregroundColor Red -text "                      </Nodes>"
		WriteHostSafe -foregroundColor Red -text "                  </Deployment>"
		WriteHostSafe -foregroundColor Red -text " "
	}
	
	return $machineNames
}



