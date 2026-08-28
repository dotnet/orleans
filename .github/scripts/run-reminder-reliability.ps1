param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('None', 'AzureStorage', 'SqlServer', 'Cosmos')]
    [string] $Provider,

    [Parameter(Mandatory = $true)]
    [ValidateSet('net8.0', 'net10.0')]
    [string] $Framework,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 20)]
    [int] $Iterations,

    [Parameter(Mandatory = $true)]
    [string] $FilterQuery,

    [Parameter(Mandatory = $true)]
    [string] $Topology,

    [Parameter(Mandatory = $true)]
    [string] $Commit
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$logsDirectory = Join-Path $repositoryRoot 'logs'
$testResultsDirectory = Join-Path $repositoryRoot 'TestResults'
New-Item -ItemType Directory -Force $logsDirectory, $testResultsDirectory | Out-Null

Push-Location $repositoryRoot
try
{
    for ($iteration = 1; $iteration -le $Iterations; $iteration++)
    {
        $runId = "reminders_$($Provider.ToLowerInvariant())_${Framework}_iteration_$iteration"
        $metadataPath = Join-Path $logsDirectory "$runId.json"
        $consoleLogPath = Join-Path $logsDirectory "$runId.console.log"
        $startedAt = [DateTimeOffset]::UtcNow
        $metadata = [ordered]@{
            iteration = $iteration
            iterations = $Iterations
            framework = $Framework
            provider = $Provider
            commit = $Commit
            topology = $Topology
            filter = $FilterQuery
            startedUtc = $startedAt.ToString('O')
            status = 'running'
        }
        $metadata | ConvertTo-Json | Set-Content -Path $metadataPath

        Write-Host "::group::Reminder reliability: provider=$Provider framework=$Framework iteration=$iteration/$Iterations"
        Write-Host "Commit: $Commit"
        Write-Host "Topology: $Topology"

        $arguments = @(
            'test'
            '--solution'
            'Orleans.slnx'
            '--framework'
            $Framework
            '--filter-query'
            $FilterQuery
            '--minimum-expected-tests'
            '1'
            '--hangdump'
            '--hangdump-timeout'
            '10m'
            '--crashdump'
            '--crashdump-type'
            'Full'
            '--hangdump-type'
            'Full'
            '--report-trx'
            '--report-trx-filename'
            "$runId`_{asm}_{tfm}_{arch}.trx"
            '--max-parallel-test-modules'
            '1'
        )

        & dotnet @arguments 2>&1 | Tee-Object -FilePath $consoleLogPath
        $exitCode = $LASTEXITCODE
        Write-Host '::endgroup::'

        $metadata.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        if ($exitCode -eq 0)
        {
            $metadata.status = 'passed'
            $metadata | ConvertTo-Json | Set-Content -Path $metadataPath
            continue
        }

        $failedTests = @(
            Get-ChildItem -Path $repositoryRoot -Filter "$runId*.trx" -Recurse -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try
                    {
                        $trx = [xml](Get-Content -Raw -Path $_.FullName)
                        $trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']") |
                            ForEach-Object { $_.testName }
                    }
                    catch
                    {
                        Write-Warning "Could not read failed tests from $($_.FullName): $_"
                    }
                } |
                Sort-Object -Unique
        )
        if ($failedTests.Count -eq 0)
        {
            $failedTests = @("<not reported; dotnet test exited with code $exitCode>")
        }

        $metadata.status = 'failed'
        $metadata.exitCode = $exitCode
        $metadata.failingTests = $failedTests
        $metadata | ConvertTo-Json -Depth 3 | Set-Content -Path $metadataPath

        $failureContext = @(
            "iteration=$iteration/$Iterations"
            "framework=$Framework"
            "provider=$Provider"
            "commit=$Commit"
            "topology=$Topology"
            "failingTests=$($failedTests -join '; ')"
        )
        $failureContextPath = Join-Path $logsDirectory 'failure-context.txt'
        $failureContext | Set-Content -Path $failureContextPath

        if ($env:GITHUB_STEP_SUMMARY)
        {
            @(
                '## Reminder reliability failure'
                ''
                "- Iteration: $iteration/$Iterations"
                "- Framework: $Framework"
                "- Provider: $Provider"
                "- Commit: ``$Commit``"
                "- Topology: $Topology"
                "- Failing test(s): $($failedTests -join ', ')"
            ) | Add-Content -Path $env:GITHUB_STEP_SUMMARY
        }

        Write-Host "::error title=Reminder reliability failure::provider=$Provider framework=$Framework iteration=$iteration/$Iterations failingTests=$($failedTests -join '; ')"
        exit $exitCode
    }
}
finally
{
    Pop-Location
}
