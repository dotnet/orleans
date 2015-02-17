# ChirperClient startup script.
# This runs after all files have been deployed.


# Collect all the parameters we've been passed by TestClients, copy them into local parameters.
# $pipeline = $testParameters.Get_Item("Pipeline") 
$pipeline = 500
$totalNodes = $testParameters.Get_Item("Nodes") 
$totalEdges = $testParameters.Get_Item("Edges")
$generationTime = $testParameters.Get_Item("GenerationTime")
$nodesPerSilo = [Math]::Truncate($totalNodes / $clientCount)
$edgesPerSilo = [Math]::Truncate($totalEdges / $clientCount)
$edgesPerSilo = $edgesPerSilo - ($edgesPerSilo % $nodesPerSilo) #hack to contain the number of edges better

WriteHostSafe -foregroundcolor Green -text ("Nodes: $totalNodes  Edges: $totalEdges  Client machines: $clientCount")

$clientPath = $localTargetPath + "\ChirperClient"


#########################
# Generate graphml files.
# This is a call to ChirperNetworkGenerator, which produces a node/edge graph to use.
# Each client generates its own self-contained portion of the graph.
# For example, if two clients share 100 nodes, then each gets 50, and each of those
# 50 only has edges to other nodes within that collection.

# The graphs are created deterministically, so multiple runs with the same parameters will generate identical graphs.
for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]

    $startNode = $nodesPerSilo * $clientIndex + 1
    $startEdge = $edgesPerSilo * $clientIndex + 1
    $filename = ".\Network-$testname-$totalNodes-$totalEdges-$siloCount-$clientCount-$clientIndex.graphml"
    $argsGenerate = "/auto $nodesPerSilo $edgesPerSilo $filename $startNode $startEdge $startNode"
    $commandGenerate = "powershell -command $clientPath\ChirperNetworkGenerator.exe " + $argsGenerate + " > generatorOutput-$clientIndex.txt"

    WriteHostSafe DarkCyan -text ("Invoke-WmiMethod -path win32_process -name create -argumentlist ""$commandGenerate"", $clientPath -ComputerName {0}" -f $clientMachine.name)
    $processGenerate = Invoke-WmiMethod -path win32_process -name create -argumentlist "$commandGenerate", $clientPath -ComputerName $clientMachine.name
    WriteHostSafe Green -text ("`tStarted network generator process {0} on machine {1}." -f $process.ProcessId, $clientMachine.name)
    $clientMachine.processId += $processGenerate.ProcessId.ToString()
}

#TODO: block on these commands instead of waiting.
start-sleep -seconds $generationTime


##################################
# Start the driver on all clients.
# Arbitrarily-named files are combined with specific arguments to run the driver.
for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]

    $filename = ".\Network-$testname-$totalNodes-$totalEdges-$siloCount-$clientCount-$clientIndex.graphml"
    $args = "/rechirp 0 /pipeline $pipeline /create $filename"
    $command = "powershell -command $clientPath\ChirperNetworkDriver.exe " + $args + " > ClientOutput-$clientIndex.txt"

    # First, detect if the client is already running.  We don't terminate if this happens, but it's bad,
    # and should invalidate the test.
    $pr = [Array] ( get-wmiobject win32_process -filter "name='ChirperNetworkDriver.exe'" -ComputerName $clientMachine.name )
    if ($pr.length -gt 0)
    {
        WriteHostSafe -foregroundColor Red -text ("*** ERROR: CLIENT PROCESS IS ALREADY RUNNING ON {0} BEFORE IT SHOULD BE RUNNING!" -f $clientMachine.name)
    }
    else
    {
        WriteHostSafe -foregroundColor Green -text ("Client process is not running on {0} before it should be." -f $clientMachine.name)
    }

    # Run the process.
    WriteHostSafe DarkCyan -text ("Invoke-WmiMethod -path win32_process -name create -argumentlist ""$command"", $clientPath -ComputerName {0}" -f $clientMachine.name)
    $process = Invoke-WmiMethod -path win32_process -name create -argumentlist "$command", $clientPath -ComputerName $clientMachine.name
    WriteHostSafe Green -text ("`tStarted client process {0} on machine {1}." -f $process.ProcessId, $clientMachine.name)
	$clientMachine.processId += $process.ProcessId.ToString()
}

# Ensure that the test is running (five seconds gives enough time for it to crash or otherwise fail)
start-sleep -seconds 5
for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]
    $pr = [Array] ( get-wmiobject win32_process -filter "name='ChirperNetworkDriver.exe'" -ComputerName $clientMachine.name )
    if ($pr.length -gt 0)
    {
        WriteHostSafe -foregroundColor Green -text ("Client process is correctly running on {0} after test started." -f $clientMachine.name)
    }
    else
    {
        WriteHostSafe -foregroundColor Red -text ("*** ERROR: CLIENT PROCESS IS NOT RUNNING ON {0} AFTER TEST STARTED!" -f $clientMachine.name)
    }
}

# Wait for the specified amount of time before returning.  TestClients.ps1 will call GlobalStop to terminate this client after we return.
start-sleep -seconds $time


