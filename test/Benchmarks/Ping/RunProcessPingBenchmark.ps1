param(
    [Parameter(Mandatory = $true)]
    [string] $BenchmarksDll,
    [string] $DotnetExecutable = "dotnet",
    [int] $WarmupSeconds = 60,
    [int] $MeasurementSeconds = 120,
    [int] $SampleCount = 1,
    [int] $Concurrency = 250,
    [int] $PayloadSize = 0,
    [switch] $Latency,
    [switch] $Tls,
    [switch] $SecondarySilo,
    [string] $OutputDirectory = ".",
    [string] $PvanalyzeDll,
    [string] $DotnetCounters,
    [string] $CounterProviders = "System.Runtime",
    [ValidateSet("cpu", "gc-verbose")]
    [string] $PvanalyzeProfile = "cpu",
    [ValidateSet("both", "client", "server")]
    [string] $ProfileRole = "both"
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ProcessorTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    public static ulong[] GetPhysicalCoreMasks()
    {
        const int RelationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new List<ulong>();
            var offset = 0;
            while (offset < length)
            {
                var item = IntPtr.Add(buffer, offset);
                var size = Marshal.ReadInt32(item, 4);
                var groupCount = (ushort)Marshal.ReadInt16(item, 30);
                for (var group = 0; group < groupCount; group++)
                {
                    result.Add(unchecked((ulong)Marshal.ReadInt64(item, 32 + (group * 16))));
                }

                offset += size;
            }

            return result.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
"@

function Start-AffinitizedDotnet(
    [string[]] $Arguments,
    [UInt64] $Affinity,
    [string] $StandardOutput,
    [string] $StandardError)
{
    $quotedArguments = $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    $command = 'start "" /b /wait /high /affinity {0:X} "{1}" {2}' -f `
        $Affinity, $DotnetExecutable, ($quotedArguments -join " ")
    return Start-Process $env:ComSpec -PassThru -NoNewWindow `
        -ArgumentList @("/d", "/s", "/c", $command) `
        -RedirectStandardOutput $StandardOutput `
        -RedirectStandardError $StandardError
}

function Get-DotnetChild([System.Diagnostics.Process] $wrapper)
{
    $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($wrapper.Id)" |
        Where-Object Name -eq "dotnet.exe" |
        Select-Object -First 1
    if (-not $child)
    {
        throw "Could not find the dotnet child process for wrapper $($wrapper.Id)."
    }

    return Get-Process -Id $child.ProcessId
}

$coreMasks = [ProcessorTopology]::GetPhysicalCoreMasks()
if ($coreMasks.Count -lt 16)
{
    throw "Expected at least 16 physical cores, found $($coreMasks.Count)."
}

[UInt64] $serverMask = 0
[UInt64] $secondaryServerMask = 0
[UInt64] $clientMask = 0
for ($i = 0; $i -lt $(if ($SecondarySilo) { 4 } else { 8 }); $i++)
{
    $serverMask = $serverMask -bor $coreMasks[$i]
}

if ($SecondarySilo)
{
    for ($i = 4; $i -lt 8; $i++)
    {
        $secondaryServerMask = $secondaryServerMask -bor $coreMasks[$i]
    }
}

for ($i = 8; $i -lt 16; $i++)
{
    $clientMask = $clientMask -bor $coreMasks[$i]
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$serverOutput = Join-Path $OutputDirectory "server.log"
$serverError = Join-Path $OutputDirectory "server.err.log"
$secondaryServerOutput = Join-Path $OutputDirectory "secondary-server.log"
$secondaryServerError = Join-Path $OutputDirectory "secondary-server.err.log"
$clientOutput = Join-Path $OutputDirectory "client.log"
$clientError = Join-Path $OutputDirectory "client.err.log"

$serverDuration = $WarmupSeconds + ($MeasurementSeconds * $SampleCount) + 60
$server = Start-AffinitizedDotnet `
    @($BenchmarksDll, "ProcessPing_Server", $serverDuration, 11111, 30000, [bool]$Tls, 0) `
    $serverMask `
    $serverOutput `
    $serverError
$secondaryServer = $null
if ($SecondarySilo)
{
    $secondaryServer = Start-AffinitizedDotnet `
        @($BenchmarksDll, "ProcessPing_Server", $serverDuration, 11112, 30001, [bool]$Tls, 11111) `
        $secondaryServerMask `
        $secondaryServerOutput `
        $secondaryServerError
}

try
{
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ($server.HasExited)
        {
            throw "Server exited before becoming ready. See $serverError."
        }

        if ((Test-Path $serverOutput) -and (Select-String -Path $serverOutput -Pattern "PING_SERVER_READY" -Quiet))
        {
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not (Select-String -Path $serverOutput -Pattern "PING_SERVER_READY" -Quiet))
    {
        throw "Timed out waiting for the server to become ready."
    }

    if ($secondaryServer)
    {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while ([DateTime]::UtcNow -lt $deadline)
        {
            if ($secondaryServer.HasExited)
            {
                throw "Secondary server exited before becoming ready. See $secondaryServerError."
            }

            if ((Test-Path $secondaryServerOutput) -and (Select-String -Path $secondaryServerOutput -Pattern "PING_SERVER_READY" -Quiet))
            {
                break
            }

            Start-Sleep -Milliseconds 200
        }

        if (-not (Select-String -Path $secondaryServerOutput -Pattern "PING_SERVER_READY" -Quiet))
        {
            throw "Timed out waiting for the secondary server to become ready."
        }
    }

    $clientArguments = if ($Latency)
    {
        @($BenchmarksDll, "ProcessPing_LatencyClient", $WarmupSeconds, $MeasurementSeconds, $SampleCount, 30000, [bool]$Tls)
    }
    else
    {
        @($BenchmarksDll, "ProcessPing_Client", $WarmupSeconds, $MeasurementSeconds, $Concurrency, 30000, $SampleCount, [bool]$Tls, $PayloadSize, $(if ($SecondarySilo) { 30001 } else { 0 }))
    }
    $client = Start-AffinitizedDotnet `
        $clientArguments `
        $clientMask `
        $clientOutput `
        $clientError

    if ($PvanalyzeDll -or $DotnetCounters)
    {
        Start-Sleep -Seconds $WarmupSeconds
        $serverDotnet = Get-DotnetChild $server
        $clientDotnet = Get-DotnetChild $client
    }

    if ($PvanalyzeDll)
    {
        if ($ProfileRole -in @("both", "server"))
        {
            & dotnet $PvanalyzeDll collect --process-id $serverDotnet.Id --profile $PvanalyzeProfile --duration-seconds 30 `
                --output (Join-Path $OutputDirectory "server.nettrace")
        }

        if ($ProfileRole -in @("both", "client"))
        {
            & dotnet $PvanalyzeDll collect --process-id $clientDotnet.Id --profile $PvanalyzeProfile --duration-seconds 30 `
                --output (Join-Path $OutputDirectory "client.nettrace")
        }
    }

    if ($DotnetCounters)
    {
        $counterDuration = [TimeSpan]::FromSeconds($MeasurementSeconds * $SampleCount)
        & $DotnetCounters collect --process-id $clientDotnet.Id --counters $CounterProviders `
            --refresh-interval 1 --format csv `
            --duration $counterDuration.ToString("dd\:hh\:mm\:ss") `
            --output (Join-Path $OutputDirectory "client-counters")
    }

    $client.WaitForExit()

    if ($client.ExitCode -ne 0)
    {
        throw "Client exited with code $($client.ExitCode). See $clientError."
    }

    Get-Content $clientOutput | Select-String "PING_(ENV|SAMPLE|RESULT|LATENCY)"
}
finally
{
    if (-not $server.HasExited)
    {
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($server.Id)" |
            ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $server.Id
        $server.WaitForExit()
    }

    if ($secondaryServer -and -not $secondaryServer.HasExited)
    {
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($secondaryServer.Id)" |
            ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $secondaryServer.Id
        $secondaryServer.WaitForExit()
    }
}
