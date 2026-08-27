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
    $artifactName = "coverage_$coverageId"
    if ($expected.ContainsKey($artifactName)) {
        throw "Duplicate coverage artifact identity '$coverageId'"
    }
    $expected.Add($artifactName, "$coverageId.cobertura.xml")
}
if ($expected.Count -eq 0) {
    throw 'The expected coverage artifact set is empty'
}

$actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$testedSha = $null
foreach ($artifact in Get-ChildItem -LiteralPath $resolvedReportDirectory -Force) {
    Assert-NotReparsePoint $artifact.FullName
    if (-not $artifact.PSIsContainer) {
        throw "Unexpected file '$($artifact.Name)' in the coverage artifact directory"
    }
    if (-not $actual.Add($artifact.Name)) {
        throw "Coverage artifact '$($artifact.Name)' appears more than once"
    }
    if (-not $expected.ContainsKey($artifact.Name)) {
        continue
    }

    $contents = @(Get-ChildItem -LiteralPath $artifact.FullName -Force)
    $expectedReport = $expected[$artifact.Name]
    $coverageId = $expectedReport.Substring(0, $expectedReport.Length - '.cobertura.xml'.Length)
    $expectedMetadata = "$coverageId.coverage.json"
    $expectedContents = @($expectedMetadata, $expectedReport) | Sort-Object
    $actualContents = @($contents | ForEach-Object Name | Sort-Object)
    if ($contents.Where({ $_.PSIsContainer }, 'First').Count -gt 0 -or
        $actualContents.Count -ne $expectedContents.Count -or
        (Compare-Object $expectedContents $actualContents)) {
        throw "Coverage artifact '$($artifact.Name)' must contain only '$expectedReport' and '$expectedMetadata'"
    }
    foreach ($content in $contents) {
        Assert-NotReparsePoint $content.FullName
    }

    $metadataPath = Join-Path $artifact.FullName $expectedMetadata
    if ((Get-Item -LiteralPath $metadataPath -Force).Length -gt 1MB) {
        throw "Coverage artifact '$($artifact.Name)' metadata exceeds the 1 MB parsing limit"
    }
    try {
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    } catch {
        throw "Coverage artifact '$($artifact.Name)' contains invalid metadata: $($_.Exception.Message)"
    }
    $metadataProperties = @($metadata.PSObject.Properties.Name | Sort-Object)
    $expectedMetadataProperties = @('artifact_name', 'commit_sha', 'coverage_id', 'format_version')
    if (Compare-Object $expectedMetadataProperties $metadataProperties) {
        throw "Coverage artifact '$($artifact.Name)' contains unexpected metadata fields"
    }
    if ($metadata.format_version -ne 1 -or
        $metadata.artifact_name -ne $artifact.Name -or
        $metadata.coverage_id -ne $coverageId -or
        $metadata.commit_sha -notmatch '^[0-9a-f]{40}$') {
        throw "Coverage artifact '$($artifact.Name)' contains inconsistent metadata"
    }
    if ($null -eq $testedSha) {
        $testedSha = $metadata.commit_sha
    } elseif ($testedSha -ne $metadata.commit_sha) {
        throw "Coverage artifacts reference multiple tested commits: '$testedSha' and '$($metadata.commit_sha)'"
    }
}

$missing = @($expected.Keys.Where({ -not $actual.Contains($_) }) | Sort-Object)
$unexpected = @($actual.Where({ -not $expected.ContainsKey($_) }) | Sort-Object)
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

$summary = [ordered]@{
    artifacts = $actual.Count
    tested_sha = $testedSha
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM
Write-Output "Validated $($actual.Count) coverage artifacts."
