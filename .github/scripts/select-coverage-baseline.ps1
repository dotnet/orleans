[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RunsJson,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedSha,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._/-]+$')]
    [string] $DefaultBranch,

    [Parameter(Mandatory)]
    [string] $JsonOutput,

    [string] $WorkflowPath = '.github/workflows/ci.yml',

    [DateTimeOffset] $AsOf = [DateTimeOffset]::UtcNow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PropertyValue {
    param(
        [object] $InputObject,
        [string] $Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($property) {
        return $property.Value
    }

    return $null
}

function Get-RunIdentity {
    param([object] $Run)

    $createdAtText = Get-PropertyValue $Run 'created_at'
    $createdAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $createdAtText,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref] $createdAt
    )) {
        throw "Coverage workflow run contains invalid created_at '$createdAtText'"
    }

    $runId = 0L
    if (-not [long]::TryParse(
        [string] (Get-PropertyValue $Run 'id'),
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref] $runId
    ) -or $runId -le 0) {
        throw 'Coverage workflow run contains an invalid id'
    }

    $headSha = [string] (Get-PropertyValue $Run 'head_sha')
    if ($headSha -notmatch '^[0-9a-f]{40}$') {
        throw "Coverage workflow run contains invalid head_sha '$headSha'"
    }

    $htmlUrl = [string] (Get-PropertyValue $Run 'html_url')
    if (-not [Uri]::IsWellFormedUriString($htmlUrl, [UriKind]::Absolute)) {
        throw "Coverage workflow run contains invalid html_url '$htmlUrl'"
    }

    return [pscustomobject]@{
        CreatedAt = $createdAt
        HeadSha = $headSha
        HtmlUrl = $htmlUrl
        Id = $runId
    }
}

$runsFile = Get-Item -LiteralPath $RunsJson -Force
if ($runsFile.Length -gt 10MB) {
    throw "$RunsJson exceeds the 10 MB parsing limit"
}
try {
    $payload = Get-Content -Raw -LiteralPath $runsFile.FullName | ConvertFrom-Json
} catch {
    throw "$RunsJson contains invalid JSON: $($_.Exception.Message)"
}

$workflowRunsProperty = $payload.PSObject.Properties['workflow_runs']
if (-not $workflowRunsProperty) {
    throw "$RunsJson does not contain workflow_runs"
}

$eligibleRuns = @(
    foreach ($run in @($workflowRunsProperty.Value)) {
        if ((Get-PropertyValue $run 'path') -ne $WorkflowPath -or
            (Get-PropertyValue $run 'event') -ne 'push' -or
            (Get-PropertyValue $run 'status') -ne 'completed' -or
            (Get-PropertyValue $run 'conclusion') -ne 'success' -or
            (Get-PropertyValue $run 'head_branch') -ne $DefaultBranch) {
            continue
        }

        Get-RunIdentity $run
    }
)
$eligibleRuns = @(
    $eligibleRuns |
        Where-Object { $_.CreatedAt -le $AsOf } |
        Sort-Object -Property @{ Expression = 'CreatedAt'; Descending = $true }, @{ Expression = 'Id'; Descending = $true }
)

$status = 'missing'
$reason = "No successful $WorkflowPath push run is available for $DefaultBranch."
$selected = $null
if ($eligibleRuns.Count -gt 0) {
    $selected = $eligibleRuns | Where-Object { $_.HeadSha -eq $ExpectedSha } | Select-Object -First 1
    if ($selected) {
        $status = 'available'
        $reason = 'The current default-branch commit has a successful same-matrix coverage run.'
    } else {
        $status = 'stale'
        $selected = $eligibleRuns[0]
        $reason = "The newest successful coverage run tested $($selected.HeadSha), not current main $ExpectedSha."
    }
}

$result = [ordered]@{
    format_version = 1
    status = $status
    reason = $reason
    expected_sha = $ExpectedSha
    branch = $DefaultBranch
    workflow_path = $WorkflowPath
    baseline_sha = if ($selected) { $selected.HeadSha } else { $null }
    baseline_run_id = if ($selected) { $selected.Id } else { $null }
    baseline_run_url = if ($selected) { $selected.HtmlUrl } else { $null }
    baseline_created_at = if ($selected) { $selected.CreatedAt.ToUniversalTime().ToString('O') } else { $null }
}
$result | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM
