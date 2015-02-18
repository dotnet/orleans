$total = 0
$count = 0
$AllLastTenTotal = 0
$AllLastTenCount = 0

if (!(Test-Path ".\$runid\summary.txt"))
{
    [IO.Directory]::CreateDirectory(".\$runid")
    New-Item –ItemType file ".\$runid\summary.txt"
}
if (!(Test-Path ".\$localTestPath\summary.txt"))
{
    New-Item –ItemType file ".\$localTestPath\summary.txt"
}

Add-Content ".\$localTestPath\summary.txt" "$descriptive"
Add-Content ".\$runid\summary.txt" "$descriptive"


copy-item "$localTestPath\*\ClientOutput*.*" "$localTestPath\"

for ($clientIndex = 0; $clientIndex -lt $clientCount; $clientIndex++)
{
    $total2 = 0
    $count2 = 0
    $lastTenTotal = 0
    $lastTenCount = 0

    $clientMachine = $clientMachineNames[$clientIndex]
    $clientOutputFile = "ClientOutput-$clientIndex.txt"

    powershell "cscript /nologo extract.vbs .\$localTestPath\$clientOutputFile >.\$localTestPath\data-$clientIndex.txt"
    
    $lastTenArray = @(0) * 10
    $lastTenIndex = 0
    get-content ".\$localTestPath\data-$clientIndex.txt" | Foreach-Object {
        $lastTenArray[$lastTenIndex] = $_
        if ($lastTenCount -lt 10) { $lastTenCount = $lastTenCount + 1; }
        $lastTenIndex = ($lastTenIndex + 1) % 10
        $total = $total + $_ 
        $count = $count + 1
        
        $total2 = $total2 + $_
        $count2 = $count2 + 1
    }

    $allLastTenCount = $allLastTenCount + $lastTenCount
    foreach ($_ in $lastTenArray)
    {
        $lastTenTotal = $lastTenTotal + $_
        $allLastTenTotal = $allLastTenTotal + $_
    }
    
    if ($clientCount -gt 1)
    {
        if ($count2 -gt 0)
        {
            Add-Content ".\$localTestPath\summary.txt" ("$testname client $clientIndex : $count2 entries read, average is {0}, last 10 average is {1}" -f ($total2 / $count2), ($lastTenTotal / $lastTenCount))
            Add-Content ".\$runid\summary.txt" ("$testname client $clientIndex : $count2 entries read, average is {0}, last 10 average is {1}" -f ($total2 / $count2), ($lastTenTotal / $lastTenCount))
        }
        else
        {
            Add-Content ".\$localTestPath\summary.txt" ("$testname client $clientIndex : No log entries recorded.  Run failed, or did not run for enough time.")
            Add-Content ".\$runid\summary.txt" ("$testname client $clientIndex : No log entries recorded.  Run failed, or did not run for enough time.")
        }    
    }
}

if (!$canceled)
{
    if ($count -gt 0)
    {
        Add-Content ".\$localTestPath\summary.txt" ("$testname : $count entries read on $clientCount machines, average is {0}, last 10 average is {1}" -f (([double]$total) / [double]$count), ([double]$allLastTenTotal / [double]$allLastTenCount))
        Add-Content ".\$runid\summary.txt" ("$testname : $count entries read on $clientCount machines, average is {0}, last 10 average is {1}" -f (([double]$total) / [double]$count), ([double]$allLastTenTotal / [double]$allLastTenCount))
    }
    else
    {
        Add-Content ".\$localTestPath\summary.txt" ("$testname : No log entries recorded.  Run failed, or did not run for enough time.")
        Add-Content ".\$runid\summary.txt" ("$testname : No log entries recorded.  Run failed, or did not run for enough time.")
    }
}

Add-Content ".\$localTestPath\summary.txt" -value ""
Add-Content ".\$runid\summary.txt" -value ""