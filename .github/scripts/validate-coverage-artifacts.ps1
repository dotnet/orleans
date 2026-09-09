[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ReportDirectory,

    [Parameter(Mandatory)]
    [string] $ExpectedArtifacts,

    [Parameter(Mandatory)]
    [string] $JsonOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NotReparsePoint {
    param([string] $Path)

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

$resolvedReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
$resolvedExpectedArtifacts = (Resolve-Path -LiteralPath $ExpectedArtifacts).Path
Assert-NotReparsePoint $resolvedReportDirectory
Assert-NotReparsePoint $resolvedExpectedArtifacts

$expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadAllLines($resolvedExpectedArtifacts)) {
    $coverageId = $line.Trim()
    if (-not $coverageId) {
        continue
    }
    if ($coverageId -notmatch '^test_output_[A-Za-z0-9_.-]+$') {
        throw "Invalid coverage artifact identity '$coverageId'"
    }
    if ($coverageId -cmatch '-attempt-[1-9][0-9]*(?:-retry)?$') {
        throw "Coverage artifact identity '$coverageId' uses the reserved attempt suffix"
    }
    $artifactName = "coverage_$coverageId"
    if ($expected.ContainsKey($artifactName)) {
        throw "Duplicate coverage artifact identity '$coverageId'"
    }
    $expected.Add($artifactName, "$coverageId.cobertura.xml")
}
if ($expected.Count -eq 0) {
    throw 'The expected coverage artifact set is empty'
}
$sortedArtifactNames = @($expected.Keys)
[Array]::Sort($sortedArtifactNames, [StringComparer]::Ordinal)
$manifestText = ($sortedArtifactNames -join "`n") + "`n"
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes($manifestText)
$manifestSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant()

$actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$selected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$superseded = [Collections.Generic.List[string]]::new()
$unexpectedArtifactNames = [Collections.Generic.List[string]]::new()
$testedSha = $null
foreach ($artifact in Get-ChildItem -LiteralPath $resolvedReportDirectory -Force) {
    Assert-NotReparsePoint $artifact.FullName
    if (-not $artifact.PSIsContainer) {
        throw "Unexpected file '$($artifact.Name)' in the coverage artifact directory"
    }

    $artifactName = $artifact.Name
    $artifactAttempt = 0
    $artifactIsRetry = $false
    if ($artifact.Name -cmatch '^(?<name>.+)-attempt-(?<attempt>[1-9][0-9]*)(?<retry>-retry)?$') {
        $artifactName = $Matches.name
        if (-not [int]::TryParse($Matches.attempt, [ref] $artifactAttempt)) {
            throw "Coverage artifact '$($artifact.Name)' has an invalid run attempt"
        }
        $artifactIsRetry = $Matches.ContainsKey('retry') -and $Matches.retry -eq '-retry'
    }
    [void] $actual.Add($artifactName)
    if (-not $expected.ContainsKey($artifactName)) {
        $unexpectedArtifactNames.Add($artifact.Name)
        continue
    }

    $contents = @(Get-ChildItem -LiteralPath $artifact.FullName -Force)
    $expectedReport = $expected[$artifactName]
    $coverageId = $expectedReport.Substring(0, $expectedReport.Length - '.cobertura.xml'.Length)
    $expectedMetadata = "$coverageId.coverage.json"
    $expectedContents = @($expectedMetadata, $expectedReport) | Sort-Object
    $actualContents = @($contents | Select-Object -ExpandProperty Name | Sort-Object)
    if ($contents.Where({ $_.PSIsContainer }, 'First').Count -gt 0 -or
        $actualContents.Count -ne $expectedContents.Count -or
        (Compare-Object $expectedContents $actualContents)) {
        throw "Coverage artifact '$artifactName' must contain only '$expectedReport' and '$expectedMetadata'"
    }
    foreach ($content in $contents) {
        Assert-NotReparsePoint $content.FullName
    }

    $metadataPath = Join-Path $artifact.FullName $expectedMetadata
    if ((Get-Item -LiteralPath $metadataPath -Force).Length -gt 1MB) {
        throw "Coverage artifact '$artifactName' metadata exceeds the 1 MB parsing limit"
    }
    try {
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    } catch {
        throw "Coverage artifact '$artifactName' contains invalid metadata: $($_.Exception.Message)"
    }
    $metadataProperties = @($metadata.PSObject.Properties.Name | Sort-Object)
    $expectedMetadataProperties = @('artifact_name', 'commit_sha', 'coverage_id', 'format_version')
    if (Compare-Object $expectedMetadataProperties $metadataProperties) {
        throw "Coverage artifact '$artifactName' contains unexpected metadata fields"
    }
    if ($metadata.format_version -ne 1 -or
        $metadata.artifact_name -ne $artifactName -or
        $metadata.coverage_id -ne $coverageId -or
        $metadata.commit_sha -notmatch '^[0-9a-f]{40}$') {
        throw "Coverage artifact '$artifactName' contains inconsistent metadata"
    }
    if ($null -eq $testedSha) {
        $testedSha = $metadata.commit_sha
    } elseif ($testedSha -ne $metadata.commit_sha) {
        throw "Coverage artifacts reference multiple tested commits: '$testedSha' and '$($metadata.commit_sha)'"
    }

    $existingArtifact = $null
    if (-not $selected.TryGetValue($artifactName, [ref] $existingArtifact)) {
        $selected.Add($artifactName, [pscustomobject]@{
            Attempt = $artifactAttempt
            IsRetry = $artifactIsRetry
            Path = $artifact.FullName
        })
    } elseif ($artifactAttempt -gt $existingArtifact.Attempt -or
        ($artifactAttempt -eq $existingArtifact.Attempt -and $artifactIsRetry -and -not $existingArtifact.IsRetry)) {
        $superseded.Add($existingArtifact.Path)
        $selected[$artifactName] = [pscustomobject]@{
            Attempt = $artifactAttempt
            IsRetry = $artifactIsRetry
            Path = $artifact.FullName
        }
    } else {
        $superseded.Add($artifact.FullName)
    }
}

$missing = @($expected.Keys.Where({ -not $actual.Contains($_) }) | Sort-Object)
$unexpected = @($unexpectedArtifactNames | Sort-Object)
if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    $details = @()
    if ($missing.Count -gt 0) {
        $details += "Missing: $($missing -join ', ')"
    }
    if ($unexpected.Count -gt 0) {
        $details += "Unexpected: $($unexpected -join ', ')"
    }
    throw "Coverage artifact set differs from the expected CI matrix. $($details -join ' ')"
}

foreach ($path in $superseded) {
    # Aggregation recursively discovers reports, so retain only the selected run attempt.
    Remove-Item -LiteralPath $path -Recurse -Force
}

$summary = [ordered]@{
    artifacts = $actual.Count
    manifest_sha256 = $manifestSha256
    tested_sha = $testedSha
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM
Write-Output "Validated $($actual.Count) coverage artifacts."
