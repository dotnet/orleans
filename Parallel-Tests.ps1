param(
    [string[]] $directories,
    [string] $testFilter = $null)

. .\common.ps1

Install-Dotnet

$maxDegreeOfParallelism = [math]::min($env:NUMBER_OF_PROCESSORS, 4)
Write-Host "Max Job Parallelism = $maxDegreeOfParallelism"

$failed = $false

if(
    [Console]::InputEncoding -is [Text.UTF8Encoding] -and
    [Console]::InputEncoding.GetPreamble().Length -ne 0
) {
    Write-Host Setting [Console]::InputEncoding
    [Console]::InputEncoding = New-Object Text.UTF8Encoding $false
}
else
{
    Write-Host Not changing [Console]::InputEncoding
}

if ([string]::IsNullOrWhiteSpace($testFilter)) {
    $testFilter = $env:TEST_FILTERS;
}

if ([string]::IsNullOrWhiteSpace($testFilter)) {
    $testFilter = "Category=BVT|Category=SlowBVT";
}

Write-Host "Test filters: `"$testFilter`"";

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

$ExecuteCmd =
{
    param([string] $args1, [string] $path)

    Set-Location -Path "$path"

    $cmdline = "& dotnet " + $args1

    Invoke-Expression $cmdline;
    $cmdExitCode = $LASTEXITCODE;
    if ($cmdExitCode -ne 0)
    {
        Throw "Error when running tests. Command: `"$cmdline`". Exit Code: $cmdExitCode"
    }
    else
    {
        Write-Host "Tests completed. Command: `"$cmdline`""
    }
}

foreach ($d in $directories)
{
    $running = @(Get-Job | Where-Object { $_.State -eq 'Running' })
    if ($running.Count -ge $maxDegreeOfParallelism) {
        $running | Wait-Job -Any | Out-Null
    }

    if (-not (Receive-CompletedJobs)) { $failed = $true }

    if (-not $testFilter.StartsWith('"')) { $testFilter = "`"$testFilter"; }
    if (-not $testFilter.EndsWith('"')) { $testFilter = "$testFilter`""; }

    $jobName = $([System.IO.Path]::GetFileName($d))
    $cmdLine = 'test --blame-hang-timeout 10m --no-build --configuration "' + $env:BuildConfiguration + '" --filter ' + $testFilter + ' --logger "trx" -- -parallel none -noshadow'
    Write-Host $jobName dotnet $cmdLine
    Start-Job $ExecuteCmd -ArgumentList @($cmdLine, "$d") -Name $jobName | Out-Null
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
