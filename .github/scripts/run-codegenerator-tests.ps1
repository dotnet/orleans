[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoveragePrefix,

    [Parameter(Mandatory)]
    [string] $Framework
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'run-dotnet-test.ps1') `
    -CoverageId "$CoveragePrefix-generator" `
    -Framework $Framework `
    -Project 'test/Orleans.CodeGenerator.Tests/Orleans.CodeGenerator.Tests.csproj' `
    -UseStaticInstrumentation `
    -ReportTrxFilename "test_results_${Framework}_{asm}_{tfm}_{arch}.trx"

& (Join-Path $PSScriptRoot 'run-dotnet-test.ps1') `
    -CoverageId "$CoveragePrefix-defaultcluster" `
    -Framework $Framework `
    -Project 'test/Orleans.DefaultCluster.Tests/Orleans.DefaultCluster.Tests.csproj' `
    -FilterQuery '/[(Provider=None)&(Area=CodeGen)&((Suite=BVT)|(Suite=SlowBVT)|(Suite=Functional))]' `
    -UseStaticInstrumentation `
    -ReportTrxFilename "test_results_defaultcluster_codegen_${Framework}_{asm}_{tfm}_{arch}.trx"
