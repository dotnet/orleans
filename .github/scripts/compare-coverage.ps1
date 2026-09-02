[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CurrentSummary,

    [Parameter(Mandatory)]
    [string] $CurrentMatrix,

    [Parameter(Mandatory)]
    [string] $BaselineSelection,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedBaselineSha,

    [Parameter(Mandatory)]
    [string] $JsonOutput,

    [Parameter(Mandatory)]
    [string] $MarkdownOutput,

    [string] $BaselineSummary,

    [string] $BaselineMatrix,

    [string] $BaselineUnavailableReason
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

function Read-JsonFile {
    param([string] $Path)

    $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -gt 1MB) {
        throw "$Path exceeds the 1 MB parsing limit"
    }
    try {
        return Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
    } catch {
        throw "$Path contains invalid JSON: $($_.Exception.Message)"
    }
}

function Get-RequiredProperty {
    param(
        [object] $InputObject,
        [string] $Name,
        [string] $Identity
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if (-not $property) {
        throw "$Identity does not contain '$Name'"
    }

    return $property.Value
}

function Get-Metrics {
    param(
        [object] $Summary,
        [string] $Prefix,
        [string] $Identity
    )

    $covered = 0L
    $total = 0L
    if (-not [long]::TryParse(
        [string] (Get-RequiredProperty $Summary "covered_$Prefix" $Identity),
        [Globalization.NumberStyles]::Integer,
        $invariantCulture,
        [ref] $covered
    ) -or
        -not [long]::TryParse(
            [string] (Get-RequiredProperty $Summary "total_$Prefix" $Identity),
            [Globalization.NumberStyles]::Integer,
            $invariantCulture,
            [ref] $total
        ) -or
        $covered -lt 0 -or
        $total -lt 0 -or
        $covered -gt $total -or
        ($Prefix -eq 'lines' -and $total -eq 0)) {
        throw "$Identity contains invalid $Prefix counts"
    }

    $rate = if ($total -gt 0) { [decimal] $covered / $total } else { [decimal] 0 }
    return [pscustomobject]@{
        Covered = $covered
        Display = ($rate * 100).ToString('F2', $invariantCulture) + '%'
        Rate = $rate
        Total = $total
    }
}

function Get-MatrixIdentity {
    param(
        [object] $Matrix,
        [string] $Identity
    )

    $testedSha = [string] (Get-RequiredProperty $Matrix 'tested_sha' $Identity)
    $manifestSha256 = [string] (Get-RequiredProperty $Matrix 'manifest_sha256' $Identity)
    if ($testedSha -notmatch '^[0-9a-f]{40}$' -or $manifestSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "$Identity contains invalid coverage identity"
    }

    return [pscustomobject]@{
        ManifestSha256 = $manifestSha256
        TestedSha = $testedSha
    }
}

function Get-Variance {
    param(
        [object] $Current,
        [object] $Baseline
    )

    $rateDelta = $Current.Rate - $Baseline.Rate
    $percentagePointDelta = $rateDelta * 100
    $classification = if ($rateDelta -gt 0) {
        'improved'
    } elseif ($rateDelta -lt 0) {
        'regressed'
    } else {
        'unchanged'
    }
    $sign = if ($percentagePointDelta -gt 0) { '+' } else { '' }

    return [pscustomobject]@{
        classification = $classification
        covered_delta = $Current.Covered - $Baseline.Covered
        percentage_point_delta = $percentagePointDelta
        percentage_point_delta_display = "$sign$($percentagePointDelta.ToString('F4', $invariantCulture)) pp"
        rate_delta = $rateDelta
        total_delta = $Current.Total - $Baseline.Total
    }
}

$currentSummaryData = Read-JsonFile $CurrentSummary
$currentMatrixData = Read-JsonFile $CurrentMatrix
$selection = Read-JsonFile $BaselineSelection
$currentLine = Get-Metrics $currentSummaryData 'lines' 'Current coverage summary'
$currentBranch = Get-Metrics $currentSummaryData 'branches' 'Current coverage summary'
$currentIdentity = Get-MatrixIdentity $currentMatrixData 'Current coverage matrix'

$selectionStatus = [string] (Get-RequiredProperty $selection 'status' 'Baseline selection')
$selectionReason = [string] (Get-RequiredProperty $selection 'reason' 'Baseline selection')
$selectionExpectedSha = [string] (Get-RequiredProperty $selection 'expected_sha' 'Baseline selection')
$baselineStatus = $selectionStatus
if ($selectionStatus -ne 'missing' -and $selectionExpectedSha -ne $ExpectedBaselineSha) {
    $baselineStatus = 'stale'
    $selectionReason = "Current main advanced from $selectionExpectedSha to $ExpectedBaselineSha while coverage was aggregated."
}
if ($BaselineUnavailableReason) {
    $baselineStatus = 'missing'
    $selectionReason = $BaselineUnavailableReason
}
if ($baselineStatus -notin 'available', 'missing', 'stale') {
    throw "Baseline selection contains unsupported status '$baselineStatus'"
}

$baselineLine = $null
$baselineBranch = $null
$lineVariance = $null
$branchVariance = $null
$overallConclusion = "baseline-$baselineStatus"
$baselineIdentity = $null
if ($baselineStatus -eq 'available') {
    if (-not $BaselineSummary -or -not $BaselineMatrix) {
        throw 'Available baseline coverage requires summary and matrix data'
    }

    $baselineSummaryData = Read-JsonFile $BaselineSummary
    $baselineMatrixData = Read-JsonFile $BaselineMatrix
    $baselineLine = Get-Metrics $baselineSummaryData 'lines' 'Baseline coverage summary'
    $baselineBranch = Get-Metrics $baselineSummaryData 'branches' 'Baseline coverage summary'
    $baselineIdentity = Get-MatrixIdentity $baselineMatrixData 'Baseline coverage matrix'
    $selectedBaselineSha = [string] (Get-RequiredProperty $selection 'baseline_sha' 'Baseline selection')
    if ($selectedBaselineSha -ne $ExpectedBaselineSha -or
        $baselineIdentity.TestedSha -ne $selectedBaselineSha) {
        throw 'Baseline coverage identity does not match current main'
    }
    if ($baselineIdentity.ManifestSha256 -ne $currentIdentity.ManifestSha256) {
        throw 'Current and baseline coverage use different matrix manifests'
    }

    $lineVariance = Get-Variance $currentLine $baselineLine
    $branchVariance = Get-Variance $currentBranch $baselineBranch
    $classifications = @($lineVariance.classification, $branchVariance.classification)
    $overallConclusion = if ($classifications -notcontains 'regressed' -and $classifications -contains 'improved') {
        'improved'
    } elseif ($classifications -notcontains 'improved' -and $classifications -contains 'regressed') {
        'regressed'
    } elseif ($classifications -contains 'improved' -and $classifications -contains 'regressed') {
        'mixed'
    } else {
        'unchanged'
    }
}

$baselineRunUrlProperty = $selection.PSObject.Properties['baseline_run_url']
$baselineRunUrl = if ($baselineRunUrlProperty) { [string] $baselineRunUrlProperty.Value } else { $null }
$result = [ordered]@{
    format_version = 1
    enforcement = 'report-only'
    conclusion = $overallConclusion
    current = [ordered]@{
        tested_sha = $currentIdentity.TestedSha
        manifest_sha256 = $currentIdentity.ManifestSha256
        line_rate = $currentLine.Rate
        line_rate_display = $currentLine.Display
        covered_lines = $currentLine.Covered
        total_lines = $currentLine.Total
        branch_rate = $currentBranch.Rate
        branch_rate_display = $currentBranch.Display
        covered_branches = $currentBranch.Covered
        total_branches = $currentBranch.Total
    }
    baseline = [ordered]@{
        status = $baselineStatus
        reason = $selectionReason
        tested_sha = if ($baselineIdentity) { $baselineIdentity.TestedSha } else { $null }
        run_url = $baselineRunUrl
        line_rate = if ($baselineLine) { $baselineLine.Rate } else { $null }
        line_rate_display = if ($baselineLine) { $baselineLine.Display } else { $null }
        covered_lines = if ($baselineLine) { $baselineLine.Covered } else { $null }
        total_lines = if ($baselineLine) { $baselineLine.Total } else { $null }
        branch_rate = if ($baselineBranch) { $baselineBranch.Rate } else { $null }
        branch_rate_display = if ($baselineBranch) { $baselineBranch.Display } else { $null }
        covered_branches = if ($baselineBranch) { $baselineBranch.Covered } else { $null }
        total_branches = if ($baselineBranch) { $baselineBranch.Total } else { $null }
    }
    variance = [ordered]@{
        lines = $lineVariance
        branches = $branchVariance
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM

$currentLineCounts = '{0:N0} / {1:N0}' -f $currentLine.Covered, $currentLine.Total
$currentBranchCounts = '{0:N0} / {1:N0}' -f $currentBranch.Covered, $currentBranch.Total
$markdown = @(
    '## Code coverage'
    ''
)
if ($baselineStatus -eq 'available') {
    $baselineLineCounts = '{0:N0} / {1:N0}' -f $baselineLine.Covered, $baselineLine.Total
    $baselineBranchCounts = '{0:N0} / {1:N0}' -f $baselineBranch.Covered, $baselineBranch.Total
    $markdown += @(
        '| Metric | Pull request | Current main | Variance |'
        '| --- | ---: | ---: | ---: |'
        "| Lines | **$($currentLine.Display)** ($currentLineCounts) | **$($baselineLine.Display)** ($baselineLineCounts) | **$($lineVariance.percentage_point_delta_display)** |"
        "| Branches | **$($currentBranch.Display)** ($currentBranchCounts) | **$($baselineBranch.Display)** ($baselineBranchCounts) | **$($branchVariance.percentage_point_delta_display)** |"
        ''
        "**Report-only conclusion:** $overallConclusion."
        ''
        "The current-main baseline is commit ``$($baselineIdentity.TestedSha.Substring(0, 10))`` and uses the same reviewed coverage matrix."
    )
} else {
    $markdown += @(
        '| Metric | Pull request |'
        '| --- | ---: |'
        "| Lines | **$($currentLine.Display)** ($currentLineCounts) |"
        "| Branches | **$($currentBranch.Display)** ($currentBranchCounts) |"
        ''
        "**Report-only conclusion:** current-main baseline $baselineStatus."
        ''
        $selectionReason
    )
}
$markdown += @(
    ''
    'Coverage combines every CI test matrix job, including providers, CodeGen, .NET 8/10, Linux, Windows, and macOS, using canonical physical source and branch identities.'
    ''
    'The comparison remains report-only while normal line and branch variance is calibrated.'
    ''
)
Set-Content -LiteralPath $MarkdownOutput -Value ($markdown -join "`n") -Encoding utf8NoBOM
