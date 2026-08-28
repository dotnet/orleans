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
$maximumSourceBytes = 10MB
$deterministicSourcePrefix = '/_/src/'
$invariantCulture = [Globalization.CultureInfo]::InvariantCulture

function Assert-NotReparsePoint {
    param([string] $Path)

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

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
    if ($pathParts.Count -eq 0 -or
        $relativePath -ne ($pathParts -join '/') -or
        $pathParts.Where({ $_ -in '.', '..' }, 'First').Count -gt 0) {
        throw "Coverage report contains invalid repository source path '$Filename'"
    }
    if ($pathParts.Where({ $_ -in 'bin', 'obj' }, 'First').Count -gt 0) {
        return $null
    }

    return [pscustomobject]@{
        RelativePath = $relativePath
        RepositoryPath = "src/$relativePath"
    }
}

function Get-SourceLineCount {
    param(
        [string] $RelativePath,
        [string] $ResolvedSourceRoot
    )

    $currentPath = $ResolvedSourceRoot
    Assert-NotReparsePoint $currentPath
    foreach ($part in $RelativePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $part
        if (-not (Test-Path -LiteralPath $currentPath)) {
            throw "Coverage report references missing source file src/$RelativePath"
        }
        Assert-NotReparsePoint $currentPath
    }

    $sourceFile = Get-Item -LiteralPath $currentPath -Force
    if (-not $sourceFile.PSIsContainer -and $sourceFile.Length -gt $maximumSourceBytes) {
        throw "$currentPath exceeds the 10 MB source validation limit"
    }
    if ($sourceFile.PSIsContainer) {
        throw "Coverage report source path src/$RelativePath is not a file"
    }

    $lineCount = 0
    $reader = [IO.File]::OpenText($sourceFile.FullName)
    try {
        while ($null -ne $reader.ReadLine()) {
            $lineCount++
        }
    } finally {
        $reader.Dispose()
    }

    return $lineCount
}

function Get-BranchCoverage {
    param([Xml.XmlElement] $LineElement)

    $conditions = @($LineElement.SelectNodes('./*[local-name()="conditions"]/*[local-name()="condition"]'))
    if ($conditions.Count -eq 0) {
        throw "Branch line $($LineElement.GetAttribute('number')) contains no conditions"
    }

    $conditionCoverage = $LineElement.GetAttribute('condition-coverage')
    if ($conditionCoverage -notmatch '^\s*(\d+(?:\.\d+)?)%\s+\((\d+)\s*/\s*(\d+)\)\s*$') {
        throw "Branch line $($LineElement.GetAttribute('number')) has invalid condition coverage '$conditionCoverage'"
    }
    $coveredBranches = [int] $Matches[2]
    $totalBranches = [int] $Matches[3]
    if ($totalBranches -ne $conditions.Count * 2 -or $coveredBranches -gt $totalBranches) {
        throw "Branch line $($LineElement.GetAttribute('number')) has inconsistent condition coverage '$conditionCoverage'"
    }

    $branches = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $conditionCoveredTotal = 0
    foreach ($condition in $conditions) {
        $conditionNumber = 0
        if (-not [int]::TryParse($condition.GetAttribute('number'), [Globalization.NumberStyles]::Integer, $invariantCulture, [ref] $conditionNumber) -or
            $conditionNumber -lt 0) {
            throw "Branch line $($LineElement.GetAttribute('number')) has an invalid condition number"
        }

        $conditionType = $condition.GetAttribute('type')
        if (-not $conditionType) {
            throw "Branch line $($LineElement.GetAttribute('number')) has a condition without a type"
        }

        $coverage = $condition.GetAttribute('coverage')
        if ($coverage -notmatch '^\s*(\d+(?:\.\d+)?)%\s*$') {
            throw "Branch line $($LineElement.GetAttribute('number')) has invalid branch coverage '$coverage'"
        }
        $coveragePercent = [decimal]::Parse($Matches[1], $invariantCulture)
        $covered = [decimal] 2 * $coveragePercent / 100
        if ($covered -lt 0 -or $covered -gt 2 -or $covered -ne [decimal]::Truncate($covered)) {
            throw "Branch line $($LineElement.GetAttribute('number')) has unsupported branch coverage '$coverage'"
        }

        $branchKey = "$conditionNumber`0$conditionType"
        if (-not $branches.TryAdd($branchKey, [int] $covered)) {
            throw "Branch line $($LineElement.GetAttribute('number')) contains duplicate condition '$conditionNumber/$conditionType'"
        }
        $conditionCoveredTotal += [int] $covered
    }
    if ($conditionCoveredTotal -ne $coveredBranches) {
        throw "Branch line $($LineElement.GetAttribute('number')) has inconsistent condition coverage '$conditionCoverage'"
    }

    return $branches
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
    $measuredBranches = [Collections.Generic.Dictionary[string, Collections.Generic.Dictionary[string, int]]]::new(
        [StringComparer]::Ordinal
    )
    $sourceLineCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($classElement in $document.SelectNodes('//*[local-name()="class"]')) {
        $filename = $classElement.GetAttribute('filename')
        if (-not $filename) {
            continue
        }

        $sourcePath = Get-RepositoryPath -Filename $filename -ResolvedSourceRoot $ResolvedSourceRoot
        if (-not $sourcePath) {
            continue
        }
        $repositoryPath = $sourcePath.RepositoryPath

        $linesElement = $classElement.SelectSingleNode('./*[local-name()="lines"]')
        if (-not $linesElement) {
            continue
        }

        $fileLines = $null
        if (-not $measuredFiles.TryGetValue($repositoryPath, [ref] $fileLines)) {
            $fileLines = [Collections.Generic.Dictionary[int, bool]]::new()
            $measuredFiles.Add($repositoryPath, $fileLines)
        }
        $fileBranches = $null
        if (-not $measuredBranches.TryGetValue($repositoryPath, [ref] $fileBranches)) {
            $fileBranches = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
            $measuredBranches.Add($repositoryPath, $fileBranches)
        }
        $sourceLineCount = 0
        if (-not $sourceLineCounts.TryGetValue($repositoryPath, [ref] $sourceLineCount)) {
            $sourceLineCount = Get-SourceLineCount -RelativePath $sourcePath.RelativePath -ResolvedSourceRoot $ResolvedSourceRoot
            $sourceLineCounts.Add($repositoryPath, $sourceLineCount)
        }

        foreach ($lineElement in $linesElement.SelectNodes('./*[local-name()="line"]')) {
            $lineNumber = 0
            $hits = 0
            if (-not [int]::TryParse($lineElement.GetAttribute('number'), [Globalization.NumberStyles]::Integer, $invariantCulture, [ref] $lineNumber) -or
                -not [int]::TryParse($lineElement.GetAttribute('hits'), [Globalization.NumberStyles]::Integer, $invariantCulture, [ref] $hits) -or
                $lineNumber -le 0 -or
                $hits -lt 0) {
                throw "$ReportPath contains invalid line coverage for $repositoryPath"
            }
            if ($lineNumber -gt $sourceLineCount) {
                throw "$ReportPath references line $lineNumber beyond the end of $repositoryPath"
            }

            $covered = $false
            [void] $fileLines.TryGetValue($lineNumber, [ref] $covered)
            $fileLines[$lineNumber] = $covered -or $hits -gt 0

            if ($lineElement.GetAttribute('branch').Equals('true', [StringComparison]::OrdinalIgnoreCase)) {
                $branches = Get-BranchCoverage -LineElement $lineElement
                foreach ($branch in $branches.GetEnumerator()) {
                    $branchKey = "$lineNumber`0$($branch.Key)"
                    $existingCovered = 0
                    [void] $fileBranches.TryGetValue($branchKey, [ref] $existingCovered)
                    $fileBranches[$branchKey] = [Math]::Max($existingCovered, $branch.Value)
                }
            }
        }
    }

    return [pscustomobject]@{
        Branches = $measuredBranches
        Files = $measuredFiles
    }
}

$resolvedReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
$resolvedSourceRootPath = (Resolve-Path -LiteralPath $SourceRoot).Path
Assert-NotReparsePoint $resolvedSourceRootPath
$resolvedSourceRoot = [IO.Path]::GetFullPath($resolvedSourceRootPath).Replace('\', '/').TrimEnd('/')
$reports = @(Get-ChildItem -LiteralPath $resolvedReportDirectory -Recurse -File -Filter '*.cobertura.xml' | Sort-Object FullName)
if ($reports.Count -eq 0) {
    throw "No Cobertura reports found under $resolvedReportDirectory"
}
if ($reports.Count -ne 1) {
    throw "Expected one merged Cobertura report, found $($reports.Count)"
}

$coverageReport = Read-CoverageReport -ReportPath $reports[0].FullName -ResolvedSourceRoot $resolvedSourceRoot
$measuredFiles = $coverageReport.Files
$measuredBranches = $coverageReport.Branches
$totalLines = 0
$coveredLines = 0
foreach ($fileLines in $measuredFiles.Values) {
    $totalLines += $fileLines.Count
    $coveredLines += @($fileLines.Values | Where-Object { $_ }).Count
}
if ($totalLines -eq 0) {
    throw 'Merged coverage report contains no measured lines under the source root'
}
$totalBranches = 0
$coveredBranches = 0
foreach ($fileBranches in $measuredBranches.Values) {
    $totalBranches += $fileBranches.Count * 2
    $coveredBranches += ($fileBranches.Values | Measure-Object -Sum).Sum
}

$lineRate = $coveredLines / $totalLines
$lineRateDisplay = ($lineRate * 100).ToString('F2', $invariantCulture) + '%'
$branchRate = if ($totalBranches -gt 0) { $coveredBranches / $totalBranches } else { 0 }
$branchRateDisplay = ($branchRate * 100).ToString('F2', $invariantCulture) + '%'
$summary = [ordered]@{
    branch_rate = $branchRate
    branch_rate_display = $branchRateDisplay
    covered_branches = $coveredBranches
    covered_lines = $coveredLines
    line_rate = $lineRate
    line_rate_display = $lineRateDisplay
    total_lines = $totalLines
    total_branches = $totalBranches
    reports = 1
    source_files = $measuredFiles.Count
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM

$coveredDisplay = $coveredLines.ToString('N0', $invariantCulture)
$totalDisplay = $totalLines.ToString('N0', $invariantCulture)
$coveredBranchesDisplay = $coveredBranches.ToString('N0', $invariantCulture)
$totalBranchesDisplay = $totalBranches.ToString('N0', $invariantCulture)
$sourceFileDisplay = $measuredFiles.Count.ToString('N0', $invariantCulture)
$markdown = @(
    '## Code coverage'
    ''
    '| Metric | Coverage | Covered |'
    '| --- | ---: | ---: |'
    "| Lines | **$lineRateDisplay** | **$coveredDisplay / $totalDisplay** |"
    "| Branches | **$branchRateDisplay** | **$coveredBranchesDisplay / $totalBranchesDisplay** |"
    ''
    "Measured $sourceFileDisplay source files from the merged coverage report."
    ''
    'Coverage combines every CI test matrix job, including providers, CodeGen, .NET 8/10, Linux, Windows, and macOS, and measures loaded repository source under `src/`.'
    ''
    'This check is report-only while coverage baselines and normal variance are calibrated.'
    ''
) -join "`n"
Set-Content -LiteralPath $MarkdownOutput -Value $markdown -Encoding utf8NoBOM
