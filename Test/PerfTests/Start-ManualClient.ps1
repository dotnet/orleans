for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]

$args = "/subscribers 1 /publishers 100 /activations 1 /pipeline 20"
$command = "powershell -command $localTargetPath\ChirperManual\ManualTest.exe " + $args + " > $localTargetPath\ChirperManual\ClientOutput-$clientIndex.txt"

    $pr = [Array] ( get-wmiobject win32_process -filter "name='ManualTest.exe'" -ComputerName $clientMachine.name )
    if ($pr.length -gt 0)
    {
        WriteHostSafe -foregroundColor Red -text ("*** ERROR: CLIENT PROCESS IS ALREADY RUNNING ON {0} BEFORE IT SHOULD BE RUNNING!" -f $clientMachine.name)
    }
    else
    {
        WriteHostSafe -foregroundColor Green -text ("Client process is not running on {0} before it should be." -f $clientMachine.name)
    }

    WriteHostSafe DarkCyan -text ("Invoke-WmiMethod -path win32_process -name create -argumentlist ""$command"", $clientPath -ComputerName {0}" -f $clientMachine.name)
    $process = Invoke-WmiMethod -path win32_process -name create -argumentlist "$command", $clientPath -ComputerName $clientMachine.name
    WriteHostSafe Green -text ("`tStarted client process {0} on machine {1}." -f $process.ProcessId, $clientMachine.name)
	$clientMachine.processId += $process.ProcessId.ToString()
}

start-sleep -seconds 5
for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]
    $pr = [Array] ( get-wmiobject win32_process -filter "name='ManualTest.exe'" -ComputerName $clientMachine.name )
    if ($pr.length -gt 0)
    {
        WriteHostSafe -foregroundColor Green -text ("Client process is correctly running on {0} after test started." -f $clientMachine.name)
    }
    else
    {
        WriteHostSafe -foregroundColor Red -text ("*** ERROR: CLIENT PROCESS IS NOT RUNNING ON {0} AFTER TEST STARTED!" -f $clientMachine.name)
    }
}

start-sleep -seconds $time



<#
# $pipeline = $testParameters.Get_Item("Pipeline") 
$pipeline = 500
$totalNodes = $testParameters.Get_Item("Nodes") 
$totalEdges = $testParameters.Get_Item("Edges")
$generationTime = $testParameters.Get_Item("GenerationTime")
$nodesPerSilo = [Math]::Truncate($totalNodes / $clientCount)
$edgesPerSilo = [Math]::Truncate($totalEdges / $clientCount)
$edgesPerSilo = $edgesPerSilo - ($edgesPerSilo % $nodesPerSilo) #hack

WriteHostSafe -foregroundcolor Green -text ("Nodes: $totalNodes  Edges: $totalEdges  Client machines: $clientCount")

$clientPath = $localTargetPath + "\ChirperClient"

# Generate graphml files
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

#todo: block
start-sleep -seconds $generationTime


for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $clientMachine = $clientMachines[$clientIndex]

    $filename = ".\Network-$testname-$totalNodes-$totalEdges-$siloCount-$clientCount-$clientIndex.graphml"
    $args = "/rechirp 0 /pipeline $pipeline /create $filename"
    $command = "powershell -command $clientPath\ChirperNetworkDriver.exe " + $args + " > ClientOutput-$clientIndex.txt"

    $pr = [Array] ( get-wmiobject win32_process -filter "name='ChirperNetworkDriver.exe'" -ComputerName $clientMachine.name )
    if ($pr.length -gt 0)
    {
        WriteHostSafe -foregroundColor Red -text ("*** ERROR: CLIENT PROCESS IS ALREADY RUNNING ON {0} BEFORE IT SHOULD BE RUNNING!" -f $clientMachine.name)
    }
    else
    {
        WriteHostSafe -foregroundColor Green -text ("Client process is not running on {0} before it should be." -f $clientMachine.name)
    }

    WriteHostSafe DarkCyan -text ("Invoke-WmiMethod -path win32_process -name create -argumentlist ""$command"", $clientPath -ComputerName {0}" -f $clientMachine.name)
    $process = Invoke-WmiMethod -path win32_process -name create -argumentlist "$command", $clientPath -ComputerName $clientMachine.name
    WriteHostSafe Green -text ("`tStarted client process {0} on machine {1}." -f $process.ProcessId, $clientMachine.name)
	$clientMachine.processId += $process.ProcessId.ToString()
}

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

start-sleep -seconds $time


#>