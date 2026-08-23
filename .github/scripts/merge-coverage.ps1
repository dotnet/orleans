[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoverageToolPath,

    [Parameter(Mandatory)]
    [int] $ExpectedArtifactCount,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [string] $ReportDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumXmlBytes = 100MB

function Assert-NotReparsePoint {
    param([IO.FileSystemInfo] $Item)

    $linkType = $Item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$($Item.FullName) must not be a symbolic link"
    }
}

$resolvedReportDirectory = Get-Item -LiteralPath $ReportDirectory -Force
Assert-NotReparsePoint $resolvedReportDirectory
$artifactDirectories = @(Get-ChildItem -LiteralPath $resolvedReportDirectory.FullName -Directory)
if ($artifactDirectories.Count -ne $ExpectedArtifactCount) {
    throw "Expected coverage from $ExpectedArtifactCount test matrix jobs, found $($artifactDirectories.Count)"
}

$coverageFiles = @(
    foreach ($artifactDirectory in $artifactDirectories) {
        Assert-NotReparsePoint $artifactDirectory
        $reports = @(Get-ChildItem -LiteralPath $artifactDirectory.FullName -Recurse -File -Filter '*.cobertura.xml')
        if ($reports.Count -eq 0) {
            throw "$($artifactDirectory.Name) contains no coverage report"
        }

        foreach ($report in $reports) {
            Assert-NotReparsePoint $report
        }

        Write-Host "Merging $($reports.Count) coverage report(s) from $($artifactDirectory.Name)."
        $reports
    }
)

$duplicateNames = @($coverageFiles | Group-Object Name | Where-Object Count -gt 1)
if ($duplicateNames.Count -gt 0) {
    $names = $duplicateNames.Name -join ', '
    throw "Coverage report names must be unique across test matrix jobs: $names"
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    if (Test-Path -LiteralPath $outputDirectory) {
        Assert-NotReparsePoint (Get-Item -LiteralPath $outputDirectory -Force)
    } else {
        [void] (New-Item -ItemType Directory -Force -Path $outputDirectory)
    }
}
if (Test-Path -LiteralPath $OutputPath) {
    Assert-NotReparsePoint (Get-Item -LiteralPath $OutputPath -Force)
}

$resolvedCoverageToolPath = (Resolve-Path -LiteralPath $CoverageToolPath).Path
& $resolvedCoverageToolPath merge $coverageFiles.FullName `
    --output $OutputPath `
    --output-format cobertura `
    --remove-input-files `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Coverage merge failed with exit code $LASTEXITCODE"
}

$output = Get-Item -LiteralPath $OutputPath -Force
Assert-NotReparsePoint $output
if ($output.Length -gt $maximumXmlBytes) {
    throw "$OutputPath exceeds the 100 MB parsing limit"
}

$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = $maximumXmlBytes
$reader = [Xml.XmlReader]::Create($output.FullName, $settings)
try {
    $coverage = [Xml.XmlDocument]::new()
    $coverage.XmlResolver = $null
    $coverage.Load($reader)
    if (-not $coverage.SelectSingleNode('//*[local-name()="line"]')) {
        throw 'Merged coverage report contains no measured lines'
    }
} finally {
    $reader.Dispose()
}
