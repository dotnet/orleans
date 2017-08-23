param(
    [string[]] $assemblies,
    [string] $testFilter,
    [string] $outDir)

$maxDegreeOfParallelism = 3
$failed = $false

function Receive-CompletedJobs {
    $succeeded = $true
    foreach($job in (Get-Job | Where-Object { $_.State -ne 'Running' }))
    {
        Receive-Job $job -AutoRemoveJob -Wait | Write-Host

        if ($job.State -eq 'Failed') { 
            $succeeded = $false
            Write-Host -ForegroundColor Red 'Failed: ' $job.Name '('$job.State')'
        }
        Write-Host ''  
    }
    return $succeeded
}

# If there is multiple xunit packages installed, take the latest one
$xunitRunner = Get-ChildItem packages -Directory -Filter "xunit.runner.console.*" | 
                ForEach-Object { Get-ChildItem $_.FullName -Recurse -File -Filter "xunit.console.exe" } | 
                Sort-Object -Property VersionInfo | 
                Select-Object -Last 1

$ExecuteCmd =
{
    param($cmd)
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0)
    {
        Throw "Error when running tests"
    }
}

foreach ($a in $assemblies)
{
    $running = @(Get-Job | Where-Object { $_.State -eq 'Running' })
    if ($running.Count -ge $maxDegreeOfParallelism) {
        $running | Wait-Job -Any | Out-Null
    }

    if (-not (Receive-CompletedJobs)) { $failed = $true }
	
    $xmlName = 'xUnit-Results-' + [System.IO.Path]::GetFileNameWithoutExtension($a) + '.xml'
    $outXml = $(Join-Path $outDir $xmlName)
    $cmdLine = $xunitRunner.FullName + ' ' + $a + ' ' + $testFilter + ' -xml ' + $outXml + ' -parallel none -noshadow -verbose' 
    Write-Host $cmdLine
    Start-Job $ExecuteCmd -ArgumentList $cmdLine -Name $([System.IO.Path]::GetFileNameWithoutExtension($a)) | Out-Null
    Write-Host ''
}

# Wait for all jobs to complete and results ready to be received
Wait-Job * | Out-Null

if (-not (Receive-CompletedJobs)) { $failed = $true }

if ($failed)
{
    Write-Host 'Test run failed'
    Exit 1
}
