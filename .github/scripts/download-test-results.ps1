[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long] $RunId,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $RunAttempt,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Sha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $env:GH_TOKEN) {
    throw 'GH_TOKEN is required to download test results.'
}

function Get-GitHubJson {
    param(
        [string] $Endpoint,
        [switch] $Paginate
    )

    $arguments = @('api', $Endpoint, '-H', 'X-GitHub-Api-Version: 2022-11-28')
    if ($Paginate) {
        $arguments += '--paginate', '--slurp'
    }

    $json = & gh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read GitHub API endpoint '$Endpoint'."
    }

    return ($json | ConvertFrom-Json)
}

function Assert-SourceRun {
    $run = Get-GitHubJson "repos/$Repository/actions/runs/$RunId"
    if ($run.id -ne $RunId -or $run.head_sha -cne $Sha -or
        $run.run_attempt -ne $RunAttempt -or $run.status -ne 'completed') {
        throw "Source run $RunId is not completed attempt $RunAttempt for commit $Sha."
    }
}

Assert-SourceRun
$pages = @(Get-GitHubJson "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100" -Paginate)
if ($pages.Count -eq 0) {
    throw "No artifact listing was returned for run $RunId."
}

$artifacts = @($pages | ForEach-Object { $_.artifacts })
$totalCount = $pages[0].total_count
if ($totalCount -ne $artifacts.Count -or @($pages | Where-Object { $_.total_count -ne $totalCount }).Count -gt 0) {
    throw "Artifact listing for run $RunId changed or is incomplete. Retry after uploads finish."
}

$seenIds = [Collections.Generic.HashSet[long]]::new()
$partitions = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($artifact in $artifacts) {
    if ($artifact.id -le 0 -or -not $seenIds.Add($artifact.id)) {
        throw "Artifact listing contains an invalid or repeated id '$($artifact.id)'."
    }
    if (-not $artifact.name.StartsWith('test_output_', [StringComparison]::Ordinal)) {
        continue
    }
    if ($artifact.name -cnotmatch '^test_output_[A-Za-z0-9_.-]+$') {
        throw "Invalid test result artifact name '$($artifact.name)'."
    }
    if ($null -eq $artifact.workflow_run -or $artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $Sha) {
        throw "Test result artifact $($artifact.id) does not belong to run $RunId for commit $Sha."
    }

    $createdAt = [DateTimeOffset]::MinValue
    if ($artifact.created_at -is [DateTime]) {
        $createdAt = [DateTimeOffset] $artifact.created_at
    } elseif (-not $artifact.created_at -or -not [DateTimeOffset]::TryParse(
        [string] $artifact.created_at,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref] $createdAt
    )) {
        throw "Test result artifact $($artifact.id) has an invalid created_at."
    }

    if (-not $partitions.ContainsKey($artifact.name)) {
        $partitions[$artifact.name] = [Collections.Generic.List[object]]::new()
    }
    $partitions[$artifact.name].Add([pscustomobject]@{ Artifact = $artifact; CreatedAt = $createdAt })
}

if ($partitions.Count -eq 0) {
    throw "No test result artifacts were found for run $RunId."
}

# The artifacts API has creation times, but no attempt field. IDs are not chronological.
# Select each partition independently so failed-only reruns retain the other partitions.
$selected = @(
    foreach ($name in @($partitions.Keys | Sort-Object)) {
        $candidates = @($partitions[$name] | Sort-Object CreatedAt -Descending)
        if ($candidates.Count -gt 1 -and $candidates[0].CreatedAt -eq $candidates[1].CreatedAt) {
            throw "Test result artifact '$name' has ambiguous latest creation times."
        }

        $artifact = $candidates[0].Artifact
        if ($artifact.expired -isnot [bool] -or $artifact.expired) {
            throw "Latest test result artifact '$name' ($($artifact.id)) is expired or has invalid expiry metadata."
        }
        $artifact
    }
)

$resolvedResultsPath = [IO.Path]::GetFullPath($ResultsPath)
if (Test-Path -LiteralPath $resolvedResultsPath) {
    throw "Test results destination '$resolvedResultsPath' already exists. Use a fresh directory."
}
[void] (New-Item -ItemType Directory -Path $resolvedResultsPath)

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $env:GH_TOKEN"
    'User-Agent' = 'dotnet-orleans-test-reporter'
    'X-GitHub-Api-Version' = '2022-11-28'
}
foreach ($artifact in $selected) {
    Write-Host "Downloading $($artifact.name) (ID: $($artifact.id), created_at: $($artifact.created_at))"
    $archive = [IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest `
            -Uri "https://api.github.com/repos/$Repository/actions/artifacts/$($artifact.id)/zip" `
            -Headers $headers `
            -OutFile $archive

        $digest = $artifact.PSObject.Properties['digest']
        if ($digest -and $digest.Value) {
            if ($digest.Value -cnotmatch '^sha256:[0-9a-f]{64}$' -or
                "sha256:$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())" -cne $digest.Value) {
                throw "Test result artifact $($artifact.id) failed SHA-256 digest validation."
            }
        } else {
            Write-Warning "Test result artifact $($artifact.id) has no API digest to verify."
        }

        $destination = Join-Path $resolvedResultsPath $artifact.name
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
        if (@(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.trx').Count -eq 0) {
            throw "Latest test result artifact '$($artifact.name)' ($($artifact.id)) contains no TRX files."
        }
    } finally {
        Remove-Item -LiteralPath $archive -Force
    }
}

Assert-SourceRun
