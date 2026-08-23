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

$maximumReportBytes = 100MB
$deterministicSourcePrefix = '/_/src/'
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

function Get-RepositoryPath {
    param(
        [string] $Filename,
        [string] $ResolvedSourceRoot
    )

    $normalized = $Filename.Replace('\', '/')
    $sourcePrefix = "$ResolvedSourceRoot/"
    if ($normalized.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $relativePath = $normalized.Substring($sourcePrefix.Length)
    } elseif ($normalized.StartsWith($deterministicSourcePrefix, [StringComparison]::Ordinal)) {
        $relativePath = $normalized.Substring($deterministicSourcePrefix.Length)
    } else {
        return $null
    }

    $pathParts = $relativePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if ($pathParts -contains '..' -or $pathParts.Where({ $_ -in 'bin', 'obj' }, 'First').Count -gt 0) {
        return $null
    }

    return "src/$relativePath"
}

function Read-CoverageReport {
    param(
        [string] $ReportPath,
        [string] $ResolvedSourceRoot
    )

    $report = Get-Item -LiteralPath $ReportPath -Force
    $linkType = $report.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($report.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$ReportPath must not be a symbolic link"
    }
    if ($report.Length -gt $maximumReportBytes) {
        throw "$ReportPath exceeds the 100 MB parsing limit"
    }

    $encoding = [Text.UTF8Encoding]::new($false, $true)
    try {
        $reportText = $encoding.GetString([IO.File]::ReadAllBytes($report.FullName))
    } catch [Text.DecoderFallbackException] {
        throw "$ReportPath must contain valid UTF-8"
    }
    if ($reportText.Length -gt 0 -and $reportText[0] -eq [char] 0xfeff) {
        $reportText = $reportText.Substring(1)
    }

    if ($reportText.IndexOf('<!DOCTYPE', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $reportText.IndexOf('<!ENTITY', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Coverage report contains unsupported XML declarations'
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $maximumReportBytes
    $stringReader = [IO.StringReader]::new($reportText)
    $reader = [Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        try {
            $document.Load($reader)
        } catch [Xml.XmlException] {
            throw "$ReportPath contains invalid XML: $($_.Exception.Message)"
        }
    } finally {
        $reader.Dispose()
        $stringReader.Dispose()
    }

    $measuredFiles = [Collections.Generic.Dictionary[string, Collections.Generic.Dictionary[int, bool]]]::new(
        [StringComparer]::Ordinal
    )
    foreach ($classElement in $document.SelectNodes('//*[local-name()="class"]')) {
        $filename = $classElement.GetAttribute('filename')
        if (-not $filename) {
            continue
        }

        $repositoryPath = Get-RepositoryPath -Filename $filename -ResolvedSourceRoot $ResolvedSourceRoot
        if (-not $repositoryPath) {
            continue
        }

        $linesElement = $classElement.SelectSingleNode('./*[local-name()="lines"]')
        if (-not $linesElement) {
            continue
        }

        $fileLines = $null
        if (-not $measuredFiles.TryGetValue($repositoryPath, [ref] $fileLines)) {
            $fileLines = [Collections.Generic.Dictionary[int, bool]]::new()
            $measuredFiles.Add($repositoryPath, $fileLines)
        }

        foreach ($lineElement in $linesElement.SelectNodes('./*[local-name()="line"]')) {
            $lineNumber = 0
            $hits = 0
            if (-not [int]::TryParse($lineElement.GetAttribute('number'), [Globalization.NumberStyles]::Integer, $invariantCulture, [ref] $lineNumber) -or
                -not [int]::TryParse($lineElement.GetAttribute('hits'), [Globalization.NumberStyles]::Integer, $invariantCulture, [ref] $hits)) {
                continue
            }

            $covered = $false
            [void] $fileLines.TryGetValue($lineNumber, [ref] $covered)
            $fileLines[$lineNumber] = $covered -or $hits -gt 0
        }
    }

    return [pscustomobject]@{ Files = $measuredFiles }
}

$resolvedReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
$resolvedSourceRoot = [IO.Path]::GetFullPath($SourceRoot).Replace('\', '/').TrimEnd('/')
$reports = @(Get-ChildItem -LiteralPath $resolvedReportDirectory -Recurse -File -Filter '*.cobertura.xml' | Sort-Object FullName)
if ($reports.Count -eq 0) {
    throw "No Cobertura reports found under $resolvedReportDirectory"
}
if ($reports.Count -ne 1) {
    throw "Expected one merged Cobertura report, found $($reports.Count)"
}

$coverageReport = Read-CoverageReport -ReportPath $reports[0].FullName -ResolvedSourceRoot $resolvedSourceRoot
$measuredFiles = $coverageReport.Files
$totalLines = 0
$coveredLines = 0
foreach ($fileLines in $measuredFiles.Values) {
    $totalLines += $fileLines.Count
    $coveredLines += @($fileLines.Values | Where-Object { $_ }).Count
}
if ($totalLines -eq 0) {
    throw 'Merged coverage report contains no measured lines under the source root'
}

$lineRate = $coveredLines / $totalLines
$lineRateDisplay = ($lineRate * 100).ToString('F2', $invariantCulture) + '%'
$summary = [ordered]@{
    covered_lines = $coveredLines
    line_rate = $lineRate
    line_rate_display = $lineRateDisplay
    total_lines = $totalLines
    reports = 1
    source_files = $measuredFiles.Count
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM

$coveredDisplay = $coveredLines.ToString('N0', $invariantCulture)
$totalDisplay = $totalLines.ToString('N0', $invariantCulture)
$sourceFileDisplay = $measuredFiles.Count.ToString('N0', $invariantCulture)
$markdown = @(
    '## Code coverage'
    ''
    '| Line coverage | Covered lines |'
    '| ---: | ---: |'
    "| **$lineRateDisplay** | **$coveredDisplay / $totalDisplay** |"
    ''
    "Measured $sourceFileDisplay source files from the merged coverage report."
    ''
    'Coverage combines the `Provider=None` BVT, SlowBVT, and Functional suites on Linux with .NET 10 and measures loaded repository source under `src/`. CodeGen tests run separately.'
    ''
) -join "`n"
Set-Content -LiteralPath $MarkdownOutput -Value $markdown -Encoding utf8NoBOM
