[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoverageId,

    [Parameter(Mandatory)]
    [string] $FilterQuery,

    [Parameter(Mandatory)]
    [string] $Framework,

    [string] $Modules,

    [string] $Projects,

    [Parameter(Mandatory)]
    [string] $ReportTrxFilename
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targets = if (-not [string]::IsNullOrWhiteSpace($Modules)) {
    @($Modules.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { @{ TestModule = $_ } })
} elseif (-not [string]::IsNullOrWhiteSpace($Projects)) {
    @($Projects.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { @{ Project = $_ } })
} else {
    throw 'Either Modules or Projects must specify at least one test target'
}

foreach ($target in $targets) {
    $path = [string] $target.Values[0]
    $targetName = [IO.Path]::GetFileNameWithoutExtension($path)
    $arguments = @{
        CoverageId = "$CoverageId-$targetName"
        FilterQuery = $FilterQuery
        Framework = $Framework
        ReportTrxFilename = $ReportTrxFilename
    }
    foreach ($entry in $target.GetEnumerator()) {
        $arguments[$entry.Key] = $entry.Value
    }

    & (Join-Path $PSScriptRoot 'run-dotnet-test.ps1') @arguments
}
