[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportDirectory,

    [Parameter(Mandatory)]
    [string] $SourceRoot,

    [Parameter(Mandatory)]
    [string] $JsonOutput,

    [Parameter(Mandatory)]
    [string] $MarkdownOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

if (-not ('Orleans.Coverage.CoverageSummaryReader' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'CoverageSummary.cs')
}

$coverage = [Orleans.Coverage.CoverageSummaryReader]::Analyze($ReportDirectory, $SourceRoot)
$lineRate = $coverage.CoveredLines / $coverage.TotalLines
$lineRateDisplay = ($lineRate * 100).ToString('F2', $invariantCulture) + '%'
$branchRate = if ($coverage.TotalBranches -gt 0) {
    $coverage.CoveredBranches / $coverage.TotalBranches
} else {
    0
}
$branchRateDisplay = ($branchRate * 100).ToString('F2', $invariantCulture) + '%'
$summary = [ordered]@{
    branch_rate = $branchRate
    branch_rate_display = $branchRateDisplay
    covered_branches = $coverage.CoveredBranches
    covered_lines = $coverage.CoveredLines
    line_rate = $lineRate
    line_rate_display = $lineRateDisplay
    total_lines = $coverage.TotalLines
    total_branches = $coverage.TotalBranches
    reports = $coverage.Reports
    source_files = $coverage.SourceFiles
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM

$coveredDisplay = $coverage.CoveredLines.ToString('N0', $invariantCulture)
$totalDisplay = $coverage.TotalLines.ToString('N0', $invariantCulture)
$coveredBranchesDisplay = $coverage.CoveredBranches.ToString('N0', $invariantCulture)
$totalBranchesDisplay = $coverage.TotalBranches.ToString('N0', $invariantCulture)
$sourceFileDisplay = $coverage.SourceFiles.ToString('N0', $invariantCulture)
$reportDisplay = $coverage.Reports.ToString('N0', $invariantCulture)
$markdown = @(
    '## Code coverage'
    ''
    '| Metric | Coverage | Covered |'
    '| --- | ---: | ---: |'
    "| Lines | **$lineRateDisplay** | **$coveredDisplay / $totalDisplay** |"
    "| Branches | **$branchRateDisplay** | **$coveredBranchesDisplay / $totalBranchesDisplay** |"
    ''
    "Measured $sourceFileDisplay source files across $reportDisplay validated coverage reports."
    ''
    'Coverage combines every CI test matrix job, including providers, CodeGen, .NET 8/10, Linux, Windows, and macOS, and measures loaded repository source under `src/`.'
    ''
    'This check is report-only while coverage baselines and normal variance are calibrated.'
    ''
) -join "`n"
Set-Content -LiteralPath $MarkdownOutput -Value $markdown -Encoding utf8NoBOM
