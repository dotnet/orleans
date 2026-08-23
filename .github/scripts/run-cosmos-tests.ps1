[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoverageId,

    [Parameter(Mandatory)]
    [string] $Framework,

    [Parameter(Mandatory)]
    [string] $Provider
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$filter = "/[(Provider=$Provider)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))&(Category!=Performance)&(Category!=Stress)]"
foreach ($attempt in 1, 2) {
    $arguments = @{
        CoverageId = $CoverageId
        Framework = $Framework
        Project = 'test/Extensions/Orleans.Cosmos.Tests/Orleans.Cosmos.Tests.csproj'
        FilterQuery = $filter
        ReportTrxFilename = "test_results_${Provider}_${Framework}_attempt${attempt}.trx"
    }
    if ($attempt -gt 1) {
        Write-Host "Cosmos tests failed on attempt $($attempt - 1); retrying once with the same filter."
        $arguments.NoBuild = $true
    }

    try {
        & (Join-Path $PSScriptRoot 'run-dotnet-test.ps1') @arguments
        return
    } catch {
        if ($attempt -eq 2) {
            throw
        }
    }
}
