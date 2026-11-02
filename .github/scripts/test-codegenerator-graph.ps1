[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('net8.0', 'net10.0')]
    [string] $Framework,
    [string] $DotnetCommand = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TestInventory {
    param([string] $Solution)

    $output = & $DotnetCommand test --solution $Solution --framework $Framework --no-build `
        --filter-query '/[(Provider=None)&(Area=CodeGen)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))]' `
        --list-tests json --max-parallel-test-modules 1
    if ($LASTEXITCODE -ne 0) {
        $output | Out-Host
        throw "Test discovery failed for $Solution ($Framework): exit code $LASTEXITCODE."
    }

    $discovery = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($discovery.version -cne '1.0') {
        throw "Unexpected test inventory version '$($discovery.version)'."
    }

    $inventory = @(
        foreach ($container in $discovery.testContainers) {
            foreach ($test in $container.tests) {
                [ordered] @{
                    assembly = $container.assemblyPath
                    framework = $container.targetFramework
                    architecture = $container.architecture
                    uid = $test.uid
                    name = $test.displayName
                    traits = @($test.traits | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 20 -Compress } | Sort-Object -CaseSensitive)
                } | ConvertTo-Json -Depth 20 -Compress
            }
        }
    )
    if ($inventory.Count -eq 0) {
        throw "Test discovery returned an empty CodeGen inventory for $Solution ($Framework)."
    }

    return $inventory
}

$full = @(Get-TestInventory -Solution 'Orleans.slnx')
$focused = @(Get-TestInventory -Solution '.github/solutions/CodeGenerator.slnf')
$differences = @(Compare-Object -ReferenceObject $full -DifferenceObject $focused -CaseSensitive)
if ($differences.Count -gt 0) {
    $differences | ForEach-Object { Write-Host "$($_.SideIndicator) $($_.InputObject)" }
    throw "CodeGen test inventories differ for $Framework."
}

Write-Output "PASS CodeGen graph preserves $($full.Count) test IDs, names, assembly/framework identities, and reported traits for $Framework."
