[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsPath,

    [Parameter(Mandatory)]
    [string] $Repository,

    [Parameter(Mandatory)]
    [string] $Sha,

    [string] $DetailsUrl,

    [string] $SummaryPath,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-MarkdownCell {
    param([AllowEmptyString()][string] $Value)

    $encodedValue = [Net.WebUtility]::HtmlEncode($Value.Replace("`r", ' ').Replace("`n", ' '))
    return $encodedValue.Replace('|', '&#124;').Replace('`', '&#96;').Replace('[', '&#91;').Replace(']', '&#93;').Replace('(', '&#40;').Replace(')', '&#41;')
}

function Get-LimitedText {
    param(
        [AllowEmptyString()][string] $Value,
        [int] $MaximumLength
    )

    if ($Value.Length -le $MaximumLength) {
        return $Value
    }

    return $Value.Substring(0, $MaximumLength - 3) + '...'
}

function Get-Utf8LimitedText {
    param(
        [AllowEmptyString()][string] $Value,
        [int] $MaximumBytes
    )

    $encoding = [Text.UTF8Encoding]::new($false)
    if ($encoding.GetByteCount($Value) -le $MaximumBytes) {
        return $Value
    }

    $suffix = '...'
    $contentLimit = $MaximumBytes - $encoding.GetByteCount($suffix)
    $minimum = 0
    $maximum = $Value.Length
    while ($minimum -lt $maximum) {
        $length = [Math]::Ceiling(($minimum + $maximum) / 2)
        if ($encoding.GetByteCount($Value.Substring(0, $length)) -le $contentLimit) {
            $minimum = $length
        } else {
            $maximum = $length - 1
        }
    }

    if ($minimum -gt 0 -and [char]::IsHighSurrogate($Value[$minimum - 1])) {
        $minimum--
    }

    return $Value.Substring(0, $minimum) + $suffix
}

function Get-SourceLocation {
    param([AllowEmptyString()][string] $StackTrace)

    foreach ($match in [regex]::Matches($StackTrace, '(?m)\s+in\s+(?<path>.+?):line\s+(?<line>\d+)\s*$')) {
        $path = $match.Groups['path'].Value.Replace('\', '/')
        $workspacePath = if ($env:GITHUB_WORKSPACE) { $env:GITHUB_WORKSPACE.Replace('\', '/').TrimEnd('/') + '/' } else { $null }
        if ($workspacePath -and $path.StartsWith($workspacePath, [StringComparison]::OrdinalIgnoreCase)) {
            $path = $path.Substring($workspacePath.Length)
        } elseif ($path.StartsWith('/_/', [StringComparison]::Ordinal)) {
            $path = $path.Substring(3)
        } else {
            $repositoryMarker = '/orleans/'
            $repositoryIndex = $path.LastIndexOf($repositoryMarker, [StringComparison]::OrdinalIgnoreCase)
            if ($repositoryIndex -ge 0) {
                $path = $path.Substring($repositoryIndex + $repositoryMarker.Length)
            } elseif ([IO.Path]::IsPathRooted($path)) {
                continue
            }
        }

        $path = $path.TrimStart('.', '/')
        if ($path -and $path.Split('/') -notcontains '..' -and $path.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) {
            return @{
                Path = $path
                Line = [int] $match.Groups['line'].Value
            }
        }
    }

    return $null
}

function Add-SummaryLine {
    param(
        [Text.StringBuilder] $Builder,
        [AllowEmptyString()][string] $Value = ''
    )

    [void] $Builder.AppendLine($Value)
}

function Get-TrxFiles {
    param([string] $RootPath)

    $directories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $directories.Push([IO.DirectoryInfo]::new($RootPath))

    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                continue
            }

            if ($item.PSIsContainer) {
                $directories.Push([IO.DirectoryInfo] $item)
            } elseif ($item.Extension.Equals('.trx', [StringComparison]::OrdinalIgnoreCase)) {
                $files.Add([IO.FileInfo] $item)
            }
        }
    }

    return @($files | Sort-Object FullName)
}

$resolvedResultsPath = (Resolve-Path -LiteralPath $ResultsPath).Path
$trxFiles = @(Get-TrxFiles -RootPath $resolvedResultsPath)
if ($trxFiles.Count -eq 0) {
    throw "No TRX files were found under '$resolvedResultsPath'."
}

$maximumTrxFileBytes = 32MB
$artifactResults = @{}
$failures = [Collections.Generic.List[object]]::new()
$runIssues = [Collections.Generic.List[object]]::new()
$parseErrors = [Collections.Generic.List[object]]::new()
$annotations = [Collections.Generic.List[object]]::new()
$utf8Encoding = [Text.UTF8Encoding]::new($false)
$passed = 0
$failed = 0
$skipped = 0

foreach ($trxFile in $trxFiles) {
    $relativePath = [IO.Path]::GetRelativePath($resolvedResultsPath, $trxFile.FullName).Replace('\', '/')
    $pathSegments = $relativePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    $artifactName = if ($pathSegments.Count -gt 1) { $pathSegments[0] } else { 'test-results' }

    if (-not $artifactResults.ContainsKey($artifactName)) {
        $artifactResults[$artifactName] = [ordered]@{
            Files = 0
            Passed = 0
            Failed = 0
            Skipped = 0
        }
    }

    $artifactResult = $artifactResults[$artifactName]
    $artifactResult.Files++

    if ($trxFile.Length -gt $maximumTrxFileBytes) {
        $parseErrors.Add([pscustomobject]@{
            File = $relativePath
            Message = "The TRX file is $($trxFile.Length) bytes, exceeding the $maximumTrxFileBytes-byte limit."
        })
        continue
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $maximumTrxFileBytes

    try {
        $reader = [Xml.XmlReader]::Create($trxFile.FullName, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        } finally {
            $reader.Dispose()
        }
    } catch [Xml.XmlException], [IO.IOException] {
        $parseErrors.Add([pscustomobject]@{
            File = $relativePath
            Message = $_.Exception.Message
        })
        continue
    }

    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace('trx', $document.DocumentElement.NamespaceURI)

    $testDefinitions = @{}
    foreach ($testDefinition in $document.SelectNodes('//trx:TestDefinitions/trx:UnitTest', $namespaceManager)) {
        $testMethod = $testDefinition.SelectSingleNode('trx:TestMethod', $namespaceManager)
        if ($testMethod) {
            $testDefinitions[$testDefinition.id] = @{
                ClassName = $testMethod.className
                Name = $testMethod.name
            }
        }
    }

    $fileFailed = 0
    foreach ($testResult in $document.SelectNodes('//trx:Results/trx:UnitTestResult', $namespaceManager)) {
        $outcome = [string] $testResult.outcome
        $isFailure = $false
        switch ($outcome) {
            'Passed' {
                $passed++
                $artifactResult.Passed++
            }
            { $_ -in 'Failed', 'Error', 'Timeout', 'Aborted', 'NotRunnable', 'Disconnected', 'PassedButRunAborted' } {
                $failed++
                $fileFailed++
                $artifactResult.Failed++
                $isFailure = $true
            }
            { $_ -in 'Inconclusive', 'NotExecuted', 'Warning', 'Completed', 'InProgress', 'Pending' } {
                $skipped++
                $artifactResult.Skipped++
            }
            default {
                $failed++
                $fileFailed++
                $artifactResult.Failed++
                $isFailure = $true
            }
        }
        if (-not $isFailure) {
            continue
        }

        $definition = $testDefinitions[[string] $testResult.testId]
        $testName = [string] $testResult.testName
        if ($definition -and $definition.ClassName) {
            $testName = "$($definition.ClassName).$($definition.Name)"
        }

        $errorInfo = $testResult.SelectSingleNode('trx:Output/trx:ErrorInfo', $namespaceManager)
        $message = if ($errorInfo) { [string] $errorInfo.Message } else { "Test outcome: $outcome" }
        $stackTrace = if ($errorInfo) { [string] $errorInfo.StackTrace } else { '' }
        $sourceLocation = Get-SourceLocation -StackTrace $stackTrace

        $failures.Add([pscustomobject]@{
            Artifact = $artifactName
            Name = $testName
            Outcome = $outcome
            Message = $message
            StackTrace = $stackTrace
            Source = $sourceLocation
        })

        if ($sourceLocation -and $annotations.Count -lt 50) {
            $annotationMessage = Get-Utf8LimitedText -Value (($message, $stackTrace | Where-Object { $_ }) -join "`n`n") -MaximumBytes 60000
            $annotations.Add(@{
                path = $sourceLocation.Path
                start_line = $sourceLocation.Line
                end_line = $sourceLocation.Line
                annotation_level = 'failure'
                title = Get-LimitedText -Value $testName -MaximumLength 255
                message = $annotationMessage
            })
        }
    }

    $resultSummary = $document.SelectSingleNode('//trx:ResultSummary', $namespaceManager)
    if ($resultSummary) {
        $summaryOutcome = [string] $resultSummary.outcome
        $counters = $resultSummary.SelectSingleNode('trx:Counters', $namespaceManager)
        $counterFailures = 0
        if ($counters) {
            foreach ($counterName in 'failed', 'error', 'timeout', 'aborted', 'notRunnable', 'disconnected', 'passedButRunAborted') {
                $counterValue = 0
                if ([int]::TryParse($counters.GetAttribute($counterName), [ref] $counterValue)) {
                    $counterFailures += $counterValue
                }
            }
        }

        $additionalFailures = [Math]::Max(0, $counterFailures - $fileFailed)
        if ($summaryOutcome -in 'Failed', 'Error', 'Timeout', 'Aborted', 'NotRunnable', 'Disconnected', 'PassedButRunAborted' -and $fileFailed -eq 0 -and $additionalFailures -eq 0) {
            $additionalFailures = 1
        }

        if ($additionalFailures -gt 0) {
            $failed += $additionalFailures
            $artifactResult.Failed += $additionalFailures

            $summaryError = $resultSummary.SelectSingleNode('trx:Output/trx:ErrorInfo/trx:Message', $namespaceManager)
            $runInfo = $resultSummary.SelectSingleNode('trx:RunInfos/trx:RunInfo', $namespaceManager)
            $issueMessage = if ($summaryError) {
                [string] $summaryError.InnerText
            } elseif ($runInfo) {
                [string] $runInfo.InnerText
            } else {
                "TRX run ended with outcome '$summaryOutcome' and $counterFailures failed run counters."
            }

            $runIssues.Add([pscustomobject]@{
                File = $relativePath
                Outcome = $summaryOutcome
                Message = $issueMessage
                Count = $additionalFailures
            })
        }
    }
}

$summary = [Text.StringBuilder]::new()
$total = $passed + $failed + $skipped
Add-SummaryLine $summary '# .NET Test Results'
Add-SummaryLine $summary
Add-SummaryLine $summary '| Passed | Failed | Skipped | Total | TRX files |'
Add-SummaryLine $summary '| ---: | ---: | ---: | ---: | ---: |'
Add-SummaryLine $summary "| $passed | $failed | $skipped | $total | $($trxFiles.Count) |"
Add-SummaryLine $summary
Add-SummaryLine $summary '## CI job results'
Add-SummaryLine $summary
Add-SummaryLine $summary '| Job | TRX files | Passed | Failed | Skipped |'
Add-SummaryLine $summary '| --- | ---: | ---: | ---: | ---: |'

foreach ($artifactName in @($artifactResults.Keys | Sort-Object)) {
    $artifactResult = $artifactResults[$artifactName]
    $displayName = Get-MarkdownCell -Value (Get-Utf8LimitedText -Value ($artifactName -replace '^test_output_', '') -MaximumBytes 500)
    Add-SummaryLine $summary "| $displayName | $($artifactResult.Files) | $($artifactResult.Passed) | $($artifactResult.Failed) | $($artifactResult.Skipped) |"
}

if ($runIssues.Count -gt 0) {
    Add-SummaryLine $summary
    Add-SummaryLine $summary '## Test runs which did not complete normally'
    Add-SummaryLine $summary
    foreach ($runIssue in $runIssues | Select-Object -First 50) {
        $file = Get-MarkdownCell -Value (Get-Utf8LimitedText -Value $runIssue.File -MaximumBytes 500)
        $outcome = Get-MarkdownCell -Value $runIssue.Outcome
        $message = Get-MarkdownCell -Value (Get-Utf8LimitedText -Value $runIssue.Message -MaximumBytes 1000)
        Add-SummaryLine $summary "- <code>$file</code>: $($runIssue.Count) additional failures ($outcome). $message"
    }
    if ($runIssues.Count -gt 50) {
        Add-SummaryLine $summary "- $($runIssues.Count - 50) additional run errors are available in the test artifacts."
    }
}

if ($parseErrors.Count -gt 0) {
    Add-SummaryLine $summary
    Add-SummaryLine $summary '## Result files which could not be parsed'
    Add-SummaryLine $summary
    foreach ($parseError in $parseErrors | Select-Object -First 50) {
        $file = Get-MarkdownCell -Value (Get-Utf8LimitedText -Value $parseError.File -MaximumBytes 500)
        $message = Get-MarkdownCell -Value (Get-Utf8LimitedText -Value $parseError.Message -MaximumBytes 1000)
        Add-SummaryLine $summary "- <code>$file</code>: $message"
    }
    if ($parseErrors.Count -gt 50) {
        Add-SummaryLine $summary "- $($parseErrors.Count - 50) additional parse errors are available in the test artifacts."
    }
}

if ($failures.Count -gt 0) {
    Add-SummaryLine $summary
    Add-SummaryLine $summary '## Failed tests'

    $omittedFailures = 0
    foreach ($failure in $failures) {
        $sourceText = if ($failure.Source) { "$($failure.Source.Path):$($failure.Source.Line)" } else { $failure.Artifact }
        $encodedSource = [Net.WebUtility]::HtmlEncode((Get-Utf8LimitedText -Value $sourceText -MaximumBytes 1000))
        $failureText = @(
            ''
            "<details><summary>$([Net.WebUtility]::HtmlEncode("$($failure.Name) [$($failure.Outcome)]"))</summary>"
            ''
            "**Source:** <code>$encodedSource</code>"
            ''
            '<pre>'
            [Net.WebUtility]::HtmlEncode((($failure.Message, $failure.StackTrace | Where-Object { $_ }) -join "`n`n"))
            '</pre>'
            '</details>'
        ) -join "`n"

        if ($utf8Encoding.GetByteCount($summary.ToString()) + $utf8Encoding.GetByteCount($failureText) -gt 62000) {
            $omittedFailures++
            continue
        }

        Add-SummaryLine $summary $failureText
    }

    if ($omittedFailures -gt 0) {
        Add-SummaryLine $summary
        Add-SummaryLine $summary "_$omittedFailures additional failures are available in the test artifacts._"
    }
} elseif ($failed -eq 0 -and $parseErrors.Count -eq 0) {
    Add-SummaryLine $summary
    Add-SummaryLine $summary 'All reported tests passed.'
}

$summaryText = Get-Utf8LimitedText -Value $summary.ToString() -MaximumBytes 65000
if ($SummaryPath) {
    Set-Content -LiteralPath $SummaryPath -Value $summaryText -Encoding utf8NoBOM
}
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summaryText -Encoding utf8NoBOM
}

$reportFailed = $failed -gt 0 -or $parseErrors.Count -gt 0 -or $total -eq 0
$conclusion = if ($reportFailed) { 'failure' } else { 'success' }
$title = if ($parseErrors.Count -gt 0) {
    "$($parseErrors.Count) result files could not be parsed"
} else {
    "$passed passed, $failed failed, $skipped skipped"
}

if ($DryRun) {
    Write-Output $summaryText
    Write-Output "Conclusion: $conclusion"
    Write-Output "Annotations: $($annotations.Count)"
    exit
}

if (-not $env:GH_TOKEN) {
    throw 'GH_TOKEN is required to create the GitHub check run.'
}

$payload = @{
    name = '.NET Test Results'
    head_sha = $Sha
    status = 'completed'
    conclusion = $conclusion
    output = @{
        title = $title
        summary = $summaryText
        annotations = @($annotations)
    }
}
if ($DetailsUrl) {
    $payload.details_url = $DetailsUrl
}

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $($env:GH_TOKEN)"
    'User-Agent' = 'dotnet-orleans-test-reporter'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$response = Invoke-RestMethod `
    -Method Post `
    -Uri "https://api.github.com/repos/$Repository/check-runs" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body ($payload | ConvertTo-Json -Depth 10)

Write-Output "Published test results check: $($response.html_url)"
