[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'summarize-coverage.ps1'
$coverageConfigPath = Join-Path $PSScriptRoot '../coverage.config.xml'
$coverageInputFingerprintScriptPath = Join-Path $PSScriptRoot 'get-coverage-input-fingerprint.ps1'
$coverageInputsPath = Join-Path $PSScriptRoot '../coverage-inputs.txt'
$compareCoverageScriptPath = Join-Path $PSScriptRoot 'compare-coverage.ps1'
$coverageReportScriptPath = Join-Path $PSScriptRoot 'coverage-report.ps1'
$archiveTestResultsActionPath = Join-Path $PSScriptRoot '../actions/archive-test-results/action.yml'
$expectedCoverageArtifactsPath = Join-Path $PSScriptRoot '../coverage-artifacts.txt'
$azureBuildTemplatePath = Join-Path $PSScriptRoot '../../.azure/pipelines/templates/build.yaml'
$azureVariablesPath = Join-Path $PSScriptRoot '../../.azure/pipelines/templates/vars.yaml'
$dotnetTestActionPath = Join-Path $PSScriptRoot '../actions/dotnet-test/action.yml'
$invokeCoverageScriptPath = Join-Path $PSScriptRoot 'invoke-coverage.ps1'
$runTestsActionPath = Join-Path $PSScriptRoot '../actions/run-tests/action.yml'
$selectCoverageBaselineScriptPath = Join-Path $PSScriptRoot 'select-coverage-baseline.ps1'
$setupCoverageScriptPath = Join-Path $PSScriptRoot 'setup-coverage.ps1'
$testResultsWorkflowPath = Join-Path $PSScriptRoot '../workflows/test-results.yml'
$validateCoverageArtifactsScriptPath = Join-Path $PSScriptRoot 'validate-coverage-artifacts.ps1'
$workflowPath = Join-Path $PSScriptRoot '../workflows/ci.yml'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans-coverage-tests-$([guid]::NewGuid())"
$testsRun = 0

function Assert-Equal {
    param(
        $Expected,
        $Actual,
        [string] $Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-Throws {
    param(
        [scriptblock] $Action,
        [string] $Pattern
    )

    $threw = $false
    try {
        & $Action
    } catch {
        $threw = $true
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected error matching '$Pattern', actual '$($_.Exception.Message)'."
        }
    }

    if (-not $threw) {
        throw "Expected error matching '$Pattern'."
    }
}

function Assert-Matches {
    param(
        [string] $Value,
        [string] $Pattern,
        [string] $Message
    )

    if ($Value -notmatch $Pattern) {
        throw "$Message Expected content matching '$Pattern'."
    }
}

function New-TestCase {
    $caseRoot = Join-Path $temporaryRoot ([guid]::NewGuid())
    $sourceRoot = Join-Path $caseRoot 'src'
    $reportDirectory = Join-Path $caseRoot 'reports'
    [void] (New-Item -ItemType Directory -Path $sourceRoot, $reportDirectory)
    [IO.File]::WriteAllLines(
        (Join-Path $sourceRoot 'Example.cs'),
        @(
            1..30 | ForEach-Object { "line $_" }
        ),
        [Text.UTF8Encoding]::new($false)
    )
    return @{
        Root = $caseRoot
        SourceRoot = $sourceRoot
        ReportDirectory = $reportDirectory
        JsonOutput = Join-Path $caseRoot 'summary.json'
        MarkdownOutput = Join-Path $caseRoot 'summary.md'
    }
}

function Get-ReportXml {
    param(
        [string] $SourceFile,
        [string] $Lines = '<line number="10" hits="1" /><line number="11" hits="0" />'
    )

    $encodedSourceFile = [Security.SecurityElement]::Escape($SourceFile)
    return @"
<coverage xmlns="http://cobertura.sourceforge.net/xml/coverage">
  <packages>
    <package>
      <classes>
        <class name="Example" filename="$encodedSourceFile">
          <lines>$Lines</lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"@
}

function Write-Report {
    param(
        [hashtable] $TestCase,
        [string] $Xml,
        [string] $Name = 'coverage.cobertura.xml'
    )

    $path = Join-Path $TestCase.ReportDirectory $Name
    [IO.File]::WriteAllText($path, $Xml, [Text.UTF8Encoding]::new($false))
    return $path
}

function Invoke-Summarizer {
    param([hashtable] $TestCase)

    & $scriptPath `
        -ReportDirectory $TestCase.ReportDirectory `
        -SourceRoot $TestCase.SourceRoot `
        -JsonOutput $TestCase.JsonOutput `
        -MarkdownOutput $TestCase.MarkdownOutput
}

function Invoke-ArtifactValidator {
    param(
        [string] $ReportDirectory,
        [string] $ExpectedArtifacts
    )

    & $validateCoverageArtifactsScriptPath `
        -ReportDirectory $ReportDirectory `
        -ExpectedArtifacts $ExpectedArtifacts `
        -JsonOutput (Join-Path (Split-Path $ReportDirectory -Parent) 'validation.json')
}

function Write-ArtifactMetadata {
    param(
        [string] $ArtifactDirectory,
        [string] $CoverageId,
        [string] $CommitSha = '0123456789abcdef0123456789abcdef01234567'
    )

    $metadata = [ordered]@{
        artifact_name = "coverage_$CoverageId"
        commit_sha = $CommitSha
        coverage_id = $CoverageId
        format_version = 1
    }
    $metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory "$CoverageId.coverage.json") -Encoding utf8NoBOM
}

function Write-Json {
    param(
        [string] $Path,
        [object] $Value
    )

    $Value |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function New-WorkflowRun {
    param(
        [long] $Id,
        [string] $HeadSha,
        [string] $CreatedAt,
        [string] $Event = 'push',
        [string] $Status = 'completed',
        [string] $Conclusion = 'success',
        [string] $HeadBranch = 'main',
        [string] $Path = '.github/workflows/ci.yml'
    )

    return [ordered]@{
        id = $Id
        head_sha = $HeadSha
        created_at = $CreatedAt
        event = $Event
        status = $Status
        conclusion = $Conclusion
        head_branch = $HeadBranch
        path = $Path
        html_url = "https://github.com/dotnet/orleans/actions/runs/$Id"
    }
}

function New-ComparisonCase {
    param(
        [long] $CurrentCoveredLines = 9,
        [long] $CurrentTotalLines = 10,
        [long] $CurrentCoveredBranches = 7,
        [long] $CurrentTotalBranches = 8,
        [long] $BaselineCoveredLines = 8,
        [long] $BaselineTotalLines = 10,
        [long] $BaselineCoveredBranches = 6,
        [long] $BaselineTotalBranches = 8,
        [string] $BaselineStatus = 'available',
        [string] $ExpectedBaselineSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        [string] $SelectedBaselineSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        [string] $CurrentManifestSha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
        [string] $BaselineManifestSha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    )

    $testCase = New-TestCase
    $comparison = @{
        CurrentSummary = Join-Path $testCase.Root 'current-summary.json'
        CurrentMatrix = Join-Path $testCase.Root 'current-matrix.json'
        BaselineSelection = Join-Path $testCase.Root 'baseline-selection.json'
        BaselineSummary = Join-Path $testCase.Root 'baseline-summary.json'
        BaselineMatrix = Join-Path $testCase.Root 'baseline-matrix.json'
        JsonOutput = Join-Path $testCase.Root 'comparison.json'
        MarkdownOutput = Join-Path $testCase.Root 'comparison.md'
        ExpectedBaselineSha = $ExpectedBaselineSha
    }

    Write-Json $comparison.CurrentSummary ([ordered]@{
        covered_lines = $CurrentCoveredLines
        total_lines = $CurrentTotalLines
        covered_branches = $CurrentCoveredBranches
        total_branches = $CurrentTotalBranches
    })
    Write-Json $comparison.CurrentMatrix ([ordered]@{
        tested_sha = 'cccccccccccccccccccccccccccccccccccccccc'
        manifest_sha256 = $CurrentManifestSha
    })
    Write-Json $comparison.BaselineSelection ([ordered]@{
        format_version = 1
        status = $BaselineStatus
        reason = "Baseline status is $BaselineStatus."
        expected_sha = $ExpectedBaselineSha
        baseline_sha = $SelectedBaselineSha
        baseline_run_url = 'https://github.com/dotnet/orleans/actions/runs/42'
    })
    Write-Json $comparison.BaselineSummary ([ordered]@{
        covered_lines = $BaselineCoveredLines
        total_lines = $BaselineTotalLines
        covered_branches = $BaselineCoveredBranches
        total_branches = $BaselineTotalBranches
    })
    Write-Json $comparison.BaselineMatrix ([ordered]@{
        tested_sha = $SelectedBaselineSha
        manifest_sha256 = $BaselineManifestSha
    })

    return $comparison
}

function Invoke-Comparison {
    param(
        [hashtable] $Comparison,
        [string] $BaselineUnavailableReason
    )

    $parameters = @{
        CurrentSummary = $Comparison.CurrentSummary
        CurrentMatrix = $Comparison.CurrentMatrix
        BaselineSelection = $Comparison.BaselineSelection
        ExpectedBaselineSha = $Comparison.ExpectedBaselineSha
        JsonOutput = $Comparison.JsonOutput
        MarkdownOutput = $Comparison.MarkdownOutput
    }
    $selection = Get-Content -Raw $Comparison.BaselineSelection | ConvertFrom-Json
    if ($BaselineUnavailableReason) {
        $parameters.BaselineUnavailableReason = $BaselineUnavailableReason
    } elseif ($selection.status -eq 'available') {
        $parameters.BaselineSummary = $Comparison.BaselineSummary
        $parameters.BaselineMatrix = $Comparison.BaselineMatrix
    }

    & $compareCoverageScriptPath @parameters
    return Get-Content -Raw $Comparison.JsonOutput | ConvertFrom-Json
}

function Invoke-Test {
    param(
        [string] $Name,
        [scriptblock] $Test
    )

    & $Test
    $script:testsRun++
    Write-Output "PASS $Name"
}

try {
    [void] (New-Item -ItemType Directory -Path $temporaryRoot)

    Invoke-Test 'summarizes valid UTF-8 coverage' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs')))
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_lines 'Covered lines differ.'
        Assert-Equal 2 $summary.total_lines 'Total lines differ.'
        Assert-Equal '50.00%' $summary.line_rate_display 'Displayed line rate differs.'
        Assert-Equal 1 $summary.source_files 'Source file count differs.'
        Assert-Equal 0 $summary.total_branches 'Branch count differs.'
    }

    Invoke-Test 'accepts a UTF-8 BOM' {
        $testCase = New-TestCase
        $report = Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml'
        [IO.File]::WriteAllText(
            $report,
            (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs')),
            [Text.UTF8Encoding]::new($true)
        )
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 2 $summary.total_lines 'UTF-8 BOM source lines differ.'
    }

    Invoke-Test 'accepts deterministic source paths' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml '/_/src/Example.cs'))
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 2 $summary.total_lines 'Deterministic source lines differ.'
    }

    Invoke-Test 'combines duplicate line hits' {
        $testCase = New-TestCase
        $sourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Example.cs'))
        $xml = @"
<coverage><packages><package><classes>
  <class filename="$sourceFile"><lines><line number="10" hits="0" /></lines></class>
  <class filename="$sourceFile"><lines><line number="10" hits="1" /></lines></class>
</classes></package></packages></coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_lines 'Duplicate covered lines differ.'
        Assert-Equal 1 $summary.total_lines 'Duplicate total lines differ.'
    }

    Invoke-Test 'combines duplicate branch coverage conservatively' {
        $testCase = New-TestCase
        $sourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Example.cs'))
        $xml = @"
<coverage><packages><package><classes>
  <class filename="$sourceFile"><lines>
    <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)">
      <conditions><condition number="0" type="jump" coverage="50%" /></conditions>
    </line>
  </lines></class>
  <class filename="$sourceFile"><lines>
    <line number="10" hits="1" branch="True" condition-coverage="100% (2/2)">
      <conditions><condition number="0" type="jump" coverage="100%" /></conditions>
    </line>
  </lines></class>
</classes></package></packages></coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 2 $summary.covered_branches 'Covered branches differ.'
        Assert-Equal 2 $summary.total_branches 'Total branches differ.'
        Assert-Equal '100.00%' $summary.branch_rate_display 'Displayed branch rate differs.'
    }

    Invoke-Test 'supports aggregate branch counts without conditions' {
        $testCase = New-TestCase
        $lines = '<line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />'
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') $lines))
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_branches 'Aggregate covered branches differ.'
        Assert-Equal 2 $summary.total_branches 'Aggregate total branches differ.'
        Assert-Equal '50.00%' $summary.branch_rate_display 'Aggregate branch rate differs.'
    }

    Invoke-Test 'rejects branch markers without aggregate counts' {
        $testCase = New-TestCase
        $lines = '<line number="10" hits="1" branch="True" />'
        $report = Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') $lines)
        Assert-Throws `
            { Invoke-Summarizer $testCase } `
            ([regex]::Escape("$report contains invalid condition coverage '' for src/Example.cs:10"))
    }

    Invoke-Test 'combines duplicate aggregate branch coverage conservatively' {
        $testCase = New-TestCase
        $sourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Example.cs'))
        $xml = @"
<coverage><packages><package><classes>
  <class filename="$sourceFile"><lines>
    <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />
  </lines></class>
  <class filename="$sourceFile"><lines>
    <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />
  </lines></class>
</classes></package></packages></coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_branches 'Duplicate aggregate covered branches differ.'
        Assert-Equal 2 $summary.total_branches 'Duplicate aggregate total branches differ.'
    }

    Invoke-Test 'counts distinct branch sites which share a physical line' {
        $testCase = New-TestCase
        $sourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Example.cs'))
        $xml = @"
<coverage><packages><package><classes>
  <class name="ContainingType" filename="$sourceFile">
    <methods><method name="Method" signature="()"><lines>
      <line number="10" hits="1" branch="True" condition-coverage="50% (2/4)" />
    </lines></method></methods>
    <lines><line number="10" hits="1" branch="True" condition-coverage="50% (2/4)" /></lines>
  </class>
  <class name="GeneratedLambda" filename="$sourceFile">
    <methods><method name="Lambda" signature="()"><lines>
      <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />
    </lines></method></methods>
    <lines><line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" /></lines>
  </class>
</classes></package></packages></coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 3 $summary.covered_branches 'Distinct branch site covered branches differ.'
        Assert-Equal 6 $summary.total_branches 'Distinct branch site total branches differ.'
    }

    Invoke-Test 'does not double count class lines which precede methods' {
        $testCase = New-TestCase
        $sourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Example.cs'))
        $xml = @"
<coverage><packages><package><classes>
  <class name="ContainingType" filename="$sourceFile">
    <lines><line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" /></lines>
    <methods><method name="Method" signature="()"><lines>
      <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />
    </lines></method></methods>
  </class>
</classes></package></packages></coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_branches 'Reordered covered branches differ.'
        Assert-Equal 2 $summary.total_branches 'Reordered total branches differ.'
    }

    Invoke-Test 'rejects inconsistent branch coverage' {
        $testCase = New-TestCase
        $lines = @'
<line number="10" hits="1" branch="True" condition-coverage="50% (1/4)">
  <conditions><condition number="0" type="jump" coverage="50%" /></conditions>
</line>
'@
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') $lines))
        Assert-Throws { Invoke-Summarizer $testCase } 'inconsistent condition coverage'
    }

    Invoke-Test 'rejects branch percentages inconsistent with counts' {
        $testCase = New-TestCase
        $lines = '<line number="10" hits="1" branch="True" condition-coverage="0% (2/2)" />'
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') $lines))
        Assert-Throws { Invoke-Summarizer $testCase } 'inconsistent condition coverage'
    }

    Invoke-Test 'rejects excessive per-line branch counts' {
        $testCase = New-TestCase
        $lines = '<line number="10" hits="1" branch="True" condition-coverage="0% (0/1025)" />'
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') $lines))
        Assert-Throws { Invoke-Summarizer $testCase } 'inconsistent condition coverage'
    }

    Invoke-Test 'rejects branch denominator drift across reports' {
        $testCase = New-TestCase
        $sourceFile = Join-Path $testCase.SourceRoot 'Example.cs'
        [void] (Write-Report `
            $testCase `
            (Get-ReportXml $sourceFile '<line number="10" hits="1" branch="True" condition-coverage="50% (1/2)" />') `
            'first.cobertura.xml')
        $secondReport = Write-Report `
            $testCase `
            (Get-ReportXml $sourceFile '<line number="10" hits="1" branch="True" condition-coverage="25% (1/4)" />') `
            'second.cobertura.xml'
        Assert-Throws `
            { Invoke-Summarizer $testCase } `
            ([regex]::Escape("$secondReport reports 4 branches for src/Example.cs:10 at Example/<non-method>, expected 2"))
    }

    Invoke-Test 'rejects missing source files' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Missing.cs')))
        Assert-Throws { Invoke-Summarizer $testCase } 'references missing source file'
    }

    Invoke-Test 'rejects lines beyond the source file' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs') '<line number="31" hits="1" />'))
        Assert-Throws { Invoke-Summarizer $testCase } 'beyond the end'
    }

    Invoke-Test 'counts distinct source files' {
        $testCase = New-TestCase
        $sourceLines = @(1..20 | ForEach-Object { "line $_" })
        [IO.File]::WriteAllLines((Join-Path $testCase.SourceRoot 'First.cs'), $sourceLines, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllLines((Join-Path $testCase.SourceRoot 'Second.cs'), $sourceLines, [Text.UTF8Encoding]::new($false))
        $firstSourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'First.cs'))
        $secondSourceFile = [Security.SecurityElement]::Escape((Join-Path $testCase.SourceRoot 'Second.cs'))
        $xml = @"
<coverage xmlns="http://cobertura.sourceforge.net/xml/coverage">
  <packages><package><classes>
    <class filename="$firstSourceFile"><lines><line number="10" hits="1" /></lines></class>
    <class filename="$secondSourceFile"><lines><line number="20" hits="0" /></lines></class>
  </classes></package></packages>
</coverage>
"@
        [void] (Write-Report $testCase $xml)
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 2 $summary.source_files 'Distinct source file count differs.'
        Assert-Equal 2 $summary.total_lines 'Distinct source line count differs.'
    }

    Invoke-Test 'combines multiple validated reports' {
        $testCase = New-TestCase
        $sourceFile = Join-Path $testCase.SourceRoot 'Example.cs'
        [void] (Write-Report $testCase (Get-ReportXml $sourceFile '<line number="10" hits="0" />') 'first.cobertura.xml')
        [void] (Write-Report $testCase (Get-ReportXml $sourceFile '<line number="10" hits="1" />') 'second.cobertura.xml')
        Invoke-Summarizer $testCase
        $summary = Get-Content -Raw $testCase.JsonOutput | ConvertFrom-Json
        Assert-Equal 1 $summary.covered_lines 'Multiple report covered lines differ.'
        Assert-Equal 1 $summary.total_lines 'Multiple report total lines differ.'
        Assert-Equal 2 $summary.reports 'Multiple report count differs.'
    }

    Invoke-Test 'requires canonical source lines from every report' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs')) 'product.cobertura.xml')
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.Root 'test/ExampleTests.cs')) 'tests.cobertura.xml')
        Assert-Throws { Invoke-Summarizer $testCase } 'tests\.cobertura\.xml contains no measured lines under the source root'
    }

    Invoke-Test 'rejects reports without source lines' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.Root 'test/ExampleTests.cs')))
        Assert-Throws { Invoke-Summarizer $testCase } 'no measured lines'
    }

    Invoke-Test 'ignores generated build sources' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example/obj/Generated.g.cs')))
        Assert-Throws { Invoke-Summarizer $testCase } 'no measured lines'
    }

    Invoke-Test 'rejects parent traversal' {
        $testCase = New-TestCase
        [void] (Write-Report $testCase (Get-ReportXml "$($testCase.SourceRoot)/../test/ExampleTests.cs"))
        Assert-Throws { Invoke-Summarizer $testCase } 'invalid repository source path'
    }

    Invoke-Test 'rejects non-canonical repository paths' {
        foreach ($sourcePath in '/_/src//Example.cs', '/_/src/./Example.cs') {
            $testCase = New-TestCase
            [void] (Write-Report $testCase (Get-ReportXml $sourcePath))
            Assert-Throws { Invoke-Summarizer $testCase } 'invalid repository source path'
        }
    }

    Invoke-Test 'rejects invalid UTF-8' {
        $testCase = New-TestCase
        $report = Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml'
        [IO.File]::WriteAllBytes($report, [byte[]] (0xff, 0xfe, 0x00, 0x80))
        Assert-Throws { Invoke-Summarizer $testCase } 'must contain valid UTF-8'
    }

    Invoke-Test 'rejects unsupported XML declarations' {
        foreach ($declaration in '<!DOCTYPE coverage>', '<!ENTITY payload "expanded">') {
            $testCase = New-TestCase
            [void] (Write-Report $testCase "$declaration<coverage />")
            Assert-Throws { Invoke-Summarizer $testCase } 'unsupported XML declarations'
        }
    }

    Invoke-Test 'reports invalid XML path' {
        $testCase = New-TestCase
        $report = Write-Report $testCase '<coverage>'
        Assert-Throws { Invoke-Summarizer $testCase } ([regex]::Escape("$report contains invalid XML"))
    }

    Invoke-Test 'rejects UTF-16 reports' {
        $testCase = New-TestCase
        $report = Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml'
        [IO.File]::WriteAllText($report, '<coverage />', [Text.Encoding]::Unicode)
        Assert-Throws { Invoke-Summarizer $testCase } 'must contain valid UTF-8'
    }

    Invoke-Test 'rejects symbolic links' {
        $testCase = New-TestCase
        $target = Join-Path $testCase.Root 'target.cobertura.xml'
        [IO.File]::WriteAllText(
            $target,
            (Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs')),
            [Text.UTF8Encoding]::new($false)
        )
        [void] (New-Item -ItemType SymbolicLink -Path (Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml') -Target $target)
        Assert-Throws { Invoke-Summarizer $testCase } 'must not be a symbolic link'
    }

    Invoke-Test 'keeps coverage runs distinct' {
        $coverageReportScript = Get-Content -Raw -LiteralPath $coverageReportScriptPath
        Assert-Matches `
            $coverageReportScript `
            '"\$CoverageId\.cobertura\.xml"' `
            'Coverage file names must include the complete matrix identity.'
    }

    Invoke-Test 'captures Linux net8 runtime crashes' {
        $dotnetTestAction = Get-Content -Raw -LiteralPath $dotnetTestActionPath
        $archiveTestResultsAction = Get-Content -Raw -LiteralPath $archiveTestResultsActionPath
        $runTestsAction = Get-Content -Raw -LiteralPath $runTestsActionPath
        Assert-Matches `
            $dotnetTestAction `
            "if: runner\.os == 'Linux' && inputs\.framework == 'net8\.0'" `
            'Runtime crash dumps must be scoped to Linux net8 test jobs.'
        Assert-Matches `
            $dotnetTestAction `
            'DOTNET_DbgEnableMiniDump=1' `
            'Runtime crash dump collection must be enabled.'
        Assert-Matches `
            $dotnetTestAction `
            'DOTNET_DbgMiniDumpType=2' `
            'Runtime crash dumps must include the managed heap.'
        Assert-Matches `
            $dotnetTestAction `
            'New-Item -ItemType Directory -Force -Path ''\$\{\{ github\.workspace \}\}/TestResults''' `
            'The absolute runtime crash dump directory must exist before test launch.'
        Assert-Matches `
            $dotnetTestAction `
            'DOTNET_DbgMiniDumpName=\$\{\{ github\.workspace \}\}/TestResults/dotnet-test\.%p\.dmp' `
            'Runtime crash dumps must flow through the test diagnostics artifact.'
        Assert-Matches `
            $dotnetTestAction `
            'DOTNET_CreateDumpDiagnostics=1' `
            'Runtime dump creation must emit diagnostics to the job log.'
        Assert-Matches `
            $archiveTestResultsAction `
            '\*\*/TestResults/\*' `
            'Runtime crash dumps must be retained by the always-run test diagnostics artifact.'
        Assert-Matches `
            $runTestsAction `
            '(?ms)^  - name: Archive test results\r?\n    if: always\(\)' `
            'Runtime crash dump upload must run after a failed test coordinator.'
    }

    Invoke-Test 'uses external coverage collection for CI builds' {
        $dotnetTestAction = Get-Content -Raw -LiteralPath $dotnetTestActionPath
        $setupTestEnvironmentAction = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../actions/setup-test-environment/action.yml')
        $coverageReportScript = Get-Content -Raw -LiteralPath $coverageReportScriptPath
        $coverageConfig = Get-Content -Raw -LiteralPath $coverageConfigPath
        Assert-Matches `
            $dotnetTestAction `
            'invoke-coverage\.ps1' `
            'Coverage must use the external collector during CI.'
        Assert-Equal `
            0 `
            ([regex]::Matches($dotnetTestAction, 'ContinuousIntegrationBuild=false')).Count `
            'GitHub coverage must preserve continuous integration build semantics.'
        [xml] $coverageConfigXml = $coverageConfig
        $modulePaths = @($coverageConfigXml.Configuration.CodeCoverage.ModulePaths.Include.ModulePath)
        Assert-Matches `
            $coverageConfig `
            '<DeterministicReport>True</DeterministicReport>' `
            'Coverage reports must preserve deterministic source paths.'
        Assert-Matches `
            $coverageConfig `
            '<ExcludeAssembliesWithoutSources>None</ExcludeAssembliesWithoutSources>' `
            'Coverage collection must retain symbol-bearing assemblies with deterministic sources.'
        Assert-Equal 1 $modulePaths.Count 'Coverage collection must define one product assembly filter.'
        Assert-Equal '.*[\\/]Orleans\.[^\\/]*\.dll$' $modulePaths[0] 'Coverage product assembly filter differs.'
        Assert-Equal $true ('C:\repo\src\Orleans.Runtime.dll' -match $modulePaths[0]) 'Coverage collection must include Orleans assemblies.'
        Assert-Equal $false ('C:\packages\FSharp.Core.dll' -match $modulePaths[0]) 'Coverage collection must exclude FSharp.Core.'
        Assert-Equal $false ('C:\packages\StackExchange.Redis.dll' -match $modulePaths[0]) 'Coverage collection must exclude StackExchange.Redis.'
        Assert-Equal $false ('C:\repo\test\Orleans.Runtime.Tests\bin\Debug\net10.0\FSharp.Core.dll' -match $modulePaths[0]) 'Coverage collection must exclude dependencies under Orleans output directories.'
        Assert-Equal $false ('C:\repo\test\Orleans.Runtime.Tests\bin\Debug\net10.0\StackExchange.Redis.dll' -match $modulePaths[0]) 'Coverage collection must exclude dependencies under Orleans output directories.'
        Assert-Equal 0 ([regex]::Matches($dotnetTestAction, 'coverage\.static\.config\.xml|IncludeFiles|static-instrumentation')).Count 'GitHub coverage must not statically instrument test outputs.'
        Assert-Matches `
            $coverageReportScript `
            'Assert-NotReparsePoint \$coverageDirectory' `
            'Coverage collection must reject a linked output directory.'
        Assert-Matches `
            $coverageReportScript `
            'Assert-NotReparsePoint \$coverageOutput' `
            'Coverage collection must reject a linked output file.'
        Assert-Matches `
            $coverageReportScript `
            'contains no measured lines' `
            'Coverage collection must reject empty reports from successful test runs.'
        Assert-Matches `
            $dotnetTestAction `
            'dotnet test --solution Orleans\.slnx' `
            'Test partitions must use native solution discovery.'
        Assert-Equal 3 ([regex]::Matches($dotnetTestAction, "github\.event_name == 'push'.*?github\.event\.repository\.default_branch")).Count 'Current-main coverage condition count differs.'
        Assert-Matches `
            $dotnetTestAction `
            "github\.event_name != 'push' \|\| github\.ref != format\('refs/heads/\{0\}', github\.event\.repository\.default_branch\)" `
            'Current-main test jobs must use the coverage collector instead of the ordinary test path.'
        Assert-Matches `
            $setupTestEnvironmentAction `
            "inputs\.coverage == 'true'.*?github\.event_name == 'push'.*?github\.event\.repository\.default_branch" `
            'Selected current-main test jobs must install the coverage collector.'
    }

    Invoke-Test 'retries only the uninitialized coverage handle failure' {
        $testCase = New-TestCase
        $invokeCoverageScript = Get-Content -Raw -LiteralPath $invokeCoverageScriptPath
        $attemptFile = Join-Path $testCase.Root 'attempt.txt'
        $fakeCollector = Join-Path $testCase.Root 'fake-collector.ps1'
        $collectorArguments = Join-Path $testCase.Root 'collector-arguments'
        $settings = Join-Path $testCase.Root 'coverage.config.xml'
        $coverageOutput = Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml'
        $retryLog = Join-Path $testCase.Root 'logs/coverage.retry.log'
        [IO.File]::WriteAllText($settings, '<Configuration />', [Text.UTF8Encoding]::new($false))
        Assert-Matches $invokeCoverageScript 'function Assert-NotReparsePoint' 'Coverage retry logging must reject linked paths.'
        Assert-Equal 2 ([regex]::Matches($invokeCoverageScript, 'Assert-NotReparsePoint \$logDirectory')).Count 'Coverage retry log directory validation count differs.'
        Assert-Equal 1 ([regex]::Matches($invokeCoverageScript, 'Assert-NotReparsePoint \$RetryLogFile')).Count 'Coverage retry log file validation count differs.'
        Assert-Matches `
            $invokeCoverageScript `
            '(?s)function Assert-CoverageOutputPath.*?Assert-NotReparsePoint \$outputDirectory.*?Assert-NotReparsePoint \$Output' `
            'Coverage collection must reject linked output paths.'
        Assert-Equal 2 ([regex]::Matches($invokeCoverageScript, '(?m)^ {4}Assert-CoverageOutputPath\r?$')).Count 'Coverage output validation count differs.'
        [IO.File]::WriteAllText(
            $fakeCollector,
            @'
$attempt = if (Test-Path -LiteralPath $env:ORLEANS_COVERAGE_ATTEMPT_FILE) {
    [int] (Get-Content -Raw -LiteralPath $env:ORLEANS_COVERAGE_ATTEMPT_FILE)
} else {
    0
}

$attempt++
Set-Content -LiteralPath $env:ORLEANS_COVERAGE_ATTEMPT_FILE -Value $attempt
Set-Content -LiteralPath "$env:ORLEANS_COVERAGE_ARGUMENTS.$attempt.txt" -Value $args
if (($env:ORLEANS_COVERAGE_FAILURE -eq 'handle' -and $attempt -eq 1) -or
    $env:ORLEANS_COVERAGE_FAILURE -eq 'handle-always') {
    Write-Output 'Unhandled exception: One or more errors occurred. (Handle is not initialized.)'
    Write-Output 'No code coverage data available. Profiler was not initialized.'
    exit 1
}

if ($env:ORLEANS_COVERAGE_FAILURE -eq 'test') {
    Write-Output 'A test failed.'
    exit 1
}

exit 0
'@,
            [Text.UTF8Encoding]::new($false)
        )

        $previousAttemptFile = $env:ORLEANS_COVERAGE_ATTEMPT_FILE
        $previousArguments = $env:ORLEANS_COVERAGE_ARGUMENTS
        $previousFailure = $env:ORLEANS_COVERAGE_FAILURE
        try {
            $env:ORLEANS_COVERAGE_ATTEMPT_FILE = $attemptFile
            $env:ORLEANS_COVERAGE_ARGUMENTS = $collectorArguments
            $env:ORLEANS_COVERAGE_FAILURE = 'handle'
            & $invokeCoverageScriptPath `
                -Settings $settings `
                -Output $coverageOutput `
                -RetryLogFile $retryLog `
                -CoverageCommand $fakeCollector `
                -Command @('dotnet', 'test', '--forwarded-argument')
            Assert-Equal 0 $LASTEXITCODE 'The retry should succeed.'
            Assert-Equal 2 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'The collector attempt count differs.'
            $retryArguments = Get-Content -LiteralPath "$collectorArguments.2.txt"
            Assert-Equal $true ($retryArguments -contains '--forwarded-argument') 'The inner test argument must be forwarded.'
            Assert-Equal $true ($retryArguments -contains '--log-file') 'The retry must capture a collector log.'
            Assert-Equal $true ($retryArguments -contains 'Verbose') 'The retry collector log must be verbose.'

            Remove-Item -LiteralPath $attemptFile
            $env:ORLEANS_COVERAGE_FAILURE = 'test'
            & $invokeCoverageScriptPath `
                -Settings $settings `
                -Output $coverageOutput `
                -RetryLogFile $retryLog `
                -CoverageCommand $fakeCollector `
                -Command @('dotnet', 'test', '--forwarded-argument')
            Assert-Equal 1 $LASTEXITCODE 'An unrelated test failure should be preserved.'
            Assert-Equal 1 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'An unrelated failure must not be retried.'

            Remove-Item -LiteralPath $attemptFile
            $env:ORLEANS_COVERAGE_FAILURE = 'handle-always'
            & $invokeCoverageScriptPath `
                -Settings $settings `
                -Output $coverageOutput `
                -RetryLogFile $retryLog `
                -CoverageCommand $fakeCollector `
                -Command @('dotnet', 'test', '--forwarded-argument')
            Assert-Equal 1 $LASTEXITCODE 'A persistent collector failure should be preserved.'
            Assert-Equal 2 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'The collector must retry only once.'
        } finally {
            $env:ORLEANS_COVERAGE_ATTEMPT_FILE = $previousAttemptFile
            $env:ORLEANS_COVERAGE_ARGUMENTS = $previousArguments
            $env:ORLEANS_COVERAGE_FAILURE = $previousFailure
        }
    }

    Invoke-Test 'validates the exact coverage matrix before trusted aggregation' {
        $workflow = Get-Content -Raw -LiteralPath $testResultsWorkflowPath
        $validator = Get-Content -Raw -LiteralPath $validateCoverageArtifactsScriptPath
        $expectedArtifacts = @(Get-Content -LiteralPath $expectedCoverageArtifactsPath)
        Assert-Matches `
            $workflow `
            'pattern:\s*coverage_test_output_\*' `
            'Coverage reporting must download every raw coverage artifact.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'merge-multiple:\s*true')).Count 'Coverage downloads must preserve artifact identities.'
        Assert-Matches `
            $workflow `
            '(?s)Validate coverage matrix.*?Summarize coverage' `
            'Coverage artifact validation must run before aggregation.'
        Assert-Matches `
            $workflow `
            '(?s)validate-coverage-artifacts\.ps1.*?coverage-artifacts\.txt' `
            'Trusted coverage reporting must use the reviewed artifact manifest.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'dotnet-coverage merge')).Count 'The trusted reporter must preserve raw branch identities.'
        Assert-Matches `
            $validator `
            'Coverage artifact set differs from the expected CI matrix' `
            'Coverage validation must reject missing and unexpected reports.'
        Assert-Matches `
            $workflow `
            'gh api "repos/\$GITHUB_REPOSITORY/tarball/\$TESTED_SHA"' `
            'Source validation must download the exact commit recorded by every coverage artifact.'
        Assert-Matches `
            $workflow `
            '(?s)Verify tested commit.*?parents -notcontains \$env:HEAD_SHA.*?Download covered source' `
            'The trusted reporter must bind the recorded merge commit to the triggering pull request head.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'name: Checkout covered source')).Count 'The privileged reporter must not check out untrusted pull request code.'
        Assert-Equal 22 $expectedArtifacts.Count 'Expected coverage artifact count differs.'
        Assert-Equal 22 (@($expectedArtifacts | Sort-Object -Unique)).Count 'Expected coverage artifact identities must be unique.'
        Assert-Equal 0 (@($expectedArtifacts | Where-Object { $_ -notmatch 'net10\.0$' })).Count 'Coverage artifacts must target .NET 10.'
        Assert-Equal 0 (@($expectedArtifacts | Where-Object { $_ -match 'macos|windows' })).Count 'Coverage artifacts must target Linux.'
    }

    Invoke-Test 'rejects missing and unexpected coverage artifacts' {
        $testCase = New-TestCase
        $expectedArtifacts = Join-Path $testCase.Root 'expected.txt'
        [IO.File]::WriteAllLines(
            $expectedArtifacts,
            @('test_output_a', 'test_output_B'),
            [Text.UTF8Encoding]::new($false)
        )
        $firstArtifact = Join-Path $testCase.ReportDirectory 'coverage_test_output_a'
        [void] (New-Item -ItemType Directory -Path $firstArtifact)
        [IO.File]::WriteAllText(
            (Join-Path $firstArtifact 'test_output_a.cobertura.xml'),
            '<coverage />',
            [Text.UTF8Encoding]::new($false)
        )
        Write-ArtifactMetadata $firstArtifact 'test_output_a'
        Assert-Throws `
            { Invoke-ArtifactValidator $testCase.ReportDirectory $expectedArtifacts } `
            'Missing: coverage_test_output_B'

        $secondArtifact = Join-Path $testCase.ReportDirectory 'coverage_test_output_B'
        [void] (New-Item -ItemType Directory -Path $secondArtifact)
        [IO.File]::WriteAllText(
            (Join-Path $secondArtifact 'test_output_B.cobertura.xml'),
            '<coverage />',
            [Text.UTF8Encoding]::new($false)
        )
        Write-ArtifactMetadata $secondArtifact 'test_output_B'
        Invoke-ArtifactValidator $testCase.ReportDirectory $expectedArtifacts | Out-Null
        $validationPath = Join-Path $testCase.Root 'validation.json'
        $firstManifestSha = (Get-Content -Raw $validationPath | ConvertFrom-Json).manifest_sha256
        $previousCulture = [Threading.Thread]::CurrentThread.CurrentCulture
        try {
            [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('tr-TR')
            Invoke-ArtifactValidator $testCase.ReportDirectory $expectedArtifacts | Out-Null
        } finally {
            [Threading.Thread]::CurrentThread.CurrentCulture = $previousCulture
        }
        $secondManifestSha = (Get-Content -Raw $validationPath | ConvertFrom-Json).manifest_sha256
        Assert-Equal $firstManifestSha $secondManifestSha 'Artifact manifest fingerprints must use ordinal ordering.'

        $unexpectedArtifact = Join-Path $testCase.ReportDirectory 'coverage_test_output_unexpected'
        [void] (New-Item -ItemType Directory -Path $unexpectedArtifact)
        [IO.File]::WriteAllText(
            (Join-Path $unexpectedArtifact 'test_output_unexpected.cobertura.xml'),
            '<coverage />',
            [Text.UTF8Encoding]::new($false)
        )
        Write-ArtifactMetadata $unexpectedArtifact 'test_output_unexpected'
        Assert-Throws `
            { Invoke-ArtifactValidator $testCase.ReportDirectory $expectedArtifacts } `
            'Unexpected: coverage_test_output_unexpected'
    }

    Invoke-Test 'rejects coverage artifacts from different commits' {
        $testCase = New-TestCase
        $expectedArtifacts = Join-Path $testCase.Root 'expected.txt'
        [IO.File]::WriteAllLines(
            $expectedArtifacts,
            @('test_output_first', 'test_output_second'),
            [Text.UTF8Encoding]::new($false)
        )
        foreach ($coverageId in 'test_output_first', 'test_output_second') {
            $artifact = Join-Path $testCase.ReportDirectory "coverage_$coverageId"
            [void] (New-Item -ItemType Directory -Path $artifact)
            [IO.File]::WriteAllText(
                (Join-Path $artifact "$coverageId.cobertura.xml"),
                '<coverage />',
                [Text.UTF8Encoding]::new($false)
            )
            $commitSha = if ($coverageId -eq 'test_output_first') {
                '0123456789abcdef0123456789abcdef01234567'
            } else {
                '89abcdef0123456789abcdef0123456789abcdef'
            }
            Write-ArtifactMetadata $artifact $coverageId $commitSha
        }

        Assert-Throws `
            { Invoke-ArtifactValidator $testCase.ReportDirectory $expectedArtifacts } `
            'reference multiple tested commits'
    }

    Invoke-Test 'selects the newest successful exact current-main run' {
        $testCase = New-TestCase
        $expectedSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $runsJson = Join-Path $testCase.Root 'runs.json'
        $selectionJson = Join-Path $testCase.Root 'selection.json'
        Write-Json $runsJson ([ordered]@{
            workflow_runs = @(
                (New-WorkflowRun 40 $expectedSha '2026-08-31T10:00:00Z')
                (New-WorkflowRun 42 $expectedSha '2026-08-31T12:00:00Z')
                (New-WorkflowRun 43 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' '2026-08-31T13:00:00Z' -Event 'pull_request')
            )
        })

        & $selectCoverageBaselineScriptPath `
            -RunsJson $runsJson `
            -ExpectedSha $expectedSha `
            -DefaultBranch main `
            -AsOf '2026-09-01T00:00:00Z' `
            -JsonOutput $selectionJson
        $selection = Get-Content -Raw $selectionJson | ConvertFrom-Json
        Assert-Equal 'available' $selection.status 'Baseline status differs.'
        Assert-Equal 42 $selection.baseline_run_id 'Baseline run identity differs.'
        Assert-Equal $expectedSha $selection.baseline_sha 'Baseline commit identity differs.'
    }

    Invoke-Test 'reports stale current-main baseline data' {
        $testCase = New-TestCase
        $runsJson = Join-Path $testCase.Root 'runs.json'
        $selectionJson = Join-Path $testCase.Root 'selection.json'
        Write-Json $runsJson ([ordered]@{
            workflow_runs = @(
                (New-WorkflowRun 40 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' '2026-08-31T10:00:00Z')
            )
        })

        & $selectCoverageBaselineScriptPath `
            -RunsJson $runsJson `
            -ExpectedSha 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' `
            -DefaultBranch main `
            -AsOf '2026-09-01T00:00:00Z' `
            -JsonOutput $selectionJson
        $selection = Get-Content -Raw $selectionJson | ConvertFrom-Json
        Assert-Equal 'stale' $selection.status 'Stale baseline status differs.'
        Assert-Matches $selection.reason 'not current main' 'Stale baseline reason differs.'
    }

    Invoke-Test 'reports missing current-main baseline data' {
        $testCase = New-TestCase
        $runsJson = Join-Path $testCase.Root 'runs.json'
        $selectionJson = Join-Path $testCase.Root 'selection.json'
        Write-Json $runsJson ([ordered]@{
            workflow_runs = @(
                (New-WorkflowRun 40 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' '2026-08-31T10:00:00Z' -Status 'in_progress' -Conclusion $null)
            )
        })

        & $selectCoverageBaselineScriptPath `
            -RunsJson $runsJson `
            -ExpectedSha 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' `
            -DefaultBranch main `
            -AsOf '2026-09-01T00:00:00Z' `
            -JsonOutput $selectionJson
        $selection = Get-Content -Raw $selectionJson | ConvertFrom-Json
        Assert-Equal 'missing' $selection.status 'Missing baseline status differs.'
    }

    Invoke-Test 'does not select historical pull request coverage as the main baseline' {
        $testCase = New-TestCase
        $expectedSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $runsJson = Join-Path $testCase.Root 'runs.json'
        $selectionJson = Join-Path $testCase.Root 'selection.json'
        Write-Json $runsJson ([ordered]@{
            workflow_runs = @(
                (New-WorkflowRun 42 $expectedSha '2026-08-31T12:00:00Z' -Event 'pull_request')
            )
        })

        & $selectCoverageBaselineScriptPath `
            -RunsJson $runsJson `
            -ExpectedSha $expectedSha `
            -DefaultBranch main `
            -AsOf '2026-09-01T00:00:00Z' `
            -JsonOutput $selectionJson
        $selection = Get-Content -Raw $selectionJson | ConvertFrom-Json
        Assert-Equal 'missing' $selection.status 'Pull request coverage must not become the main baseline.'
        Assert-Equal $null $selection.baseline_run_id 'Pull request coverage must not publish a baseline run identity.'
    }

    Invoke-Test 'fingerprints trusted coverage inputs' {
        $testCase = New-TestCase
        $repository = Join-Path $testCase.Root 'repository'
        [void] (New-Item -ItemType Directory -Path (Join-Path $repository '.github/scripts') -Force)
        [IO.File]::WriteAllText((Join-Path $repository '.github/scripts/a.ps1'), 'first', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $repository '.github/scripts/B.ps1'), 'second', [Text.UTF8Encoding]::new($false))
        $manifest = Join-Path $testCase.Root 'inputs.txt'
        [IO.File]::WriteAllLines(
            $manifest,
            @('.github/scripts/a.ps1', '.github/scripts/B.ps1'),
            [Text.UTF8Encoding]::new($false)
        )
        $firstOutput = Join-Path $testCase.Root 'first.json'
        $reorderedOutput = Join-Path $testCase.Root 'reordered.json'
        $secondOutput = Join-Path $testCase.Root 'second.json'
        & $coverageInputFingerprintScriptPath -RepositoryRoot $repository -Manifest $manifest -JsonOutput $firstOutput
        [IO.File]::WriteAllLines(
            $manifest,
            @('.github/scripts/B.ps1', '.github/scripts/a.ps1'),
            [Text.UTF8Encoding]::new($false)
        )
        $previousCulture = [Threading.Thread]::CurrentThread.CurrentCulture
        try {
            [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('tr-TR')
            & $coverageInputFingerprintScriptPath -RepositoryRoot $repository -Manifest $manifest -JsonOutput $reorderedOutput
        } finally {
            [Threading.Thread]::CurrentThread.CurrentCulture = $previousCulture
        }
        [IO.File]::WriteAllText((Join-Path $repository '.github/scripts/a.ps1'), 'changed', [Text.UTF8Encoding]::new($false))
        & $coverageInputFingerprintScriptPath -RepositoryRoot $repository -Manifest $manifest -JsonOutput $secondOutput
        $first = Get-Content -Raw $firstOutput | ConvertFrom-Json
        $reordered = Get-Content -Raw $reorderedOutput | ConvertFrom-Json
        $second = Get-Content -Raw $secondOutput | ConvertFrom-Json
        Assert-Equal 2 $first.files 'Coverage input file count differs.'
        Assert-Equal $first.sha256 $reordered.sha256 'Coverage input fingerprints must use ordinal ordering.'
        Assert-Equal $false ($first.sha256 -eq $second.sha256) 'Coverage input changes must change the fingerprint.'
    }

    Invoke-Test 'rejects unsafe coverage input paths' {
        $testCase = New-TestCase
        $manifest = Join-Path $testCase.Root 'inputs.txt'
        [IO.File]::WriteAllText($manifest, '../ci.yml', [Text.UTF8Encoding]::new($false))
        Assert-Throws `
            {
                & $coverageInputFingerprintScriptPath `
                    -RepositoryRoot $testCase.Root `
                    -Manifest $manifest `
                    -JsonOutput (Join-Path $testCase.Root 'fingerprint.json')
            } `
            'Invalid coverage input path'
    }

    Invoke-Test 'reports line and branch variance' {
        $comparison = New-ComparisonCase
        $result = Invoke-Comparison $comparison
        Assert-Equal 'improved' $result.conclusion 'Coverage conclusion differs.'
        Assert-Equal '+10.0000 pp' $result.variance.lines.percentage_point_delta_display 'Line variance differs.'
        Assert-Equal '+12.5000 pp' $result.variance.branches.percentage_point_delta_display 'Branch variance differs.'
        Assert-Equal 1 $result.variance.lines.covered_delta 'Covered line variance differs.'
        Assert-Equal 0 $result.variance.lines.total_delta 'Total line variance differs.'
    }

    Invoke-Test 'reports mixed coverage conclusions' {
        $comparison = New-ComparisonCase `
            -CurrentCoveredBranches 5 `
            -BaselineCoveredBranches 6
        $result = Invoke-Comparison $comparison
        Assert-Equal 'mixed' $result.conclusion 'Mixed coverage conclusion differs.'
        Assert-Equal 'improved' $result.variance.lines.classification 'Line conclusion differs.'
        Assert-Equal 'regressed' $result.variance.branches.classification 'Branch conclusion differs.'
    }

    Invoke-Test 'reports regressed and unchanged coverage conclusions' {
        $regressedComparison = New-ComparisonCase `
            -CurrentCoveredLines 7 `
            -CurrentCoveredBranches 5
        $regressed = Invoke-Comparison $regressedComparison
        Assert-Equal 'regressed' $regressed.conclusion 'Regressed coverage conclusion differs.'

        $unchangedComparison = New-ComparisonCase `
            -CurrentCoveredLines 8 `
            -CurrentCoveredBranches 6
        $unchanged = Invoke-Comparison $unchangedComparison
        Assert-Equal 'unchanged' $unchanged.conclusion 'Unchanged coverage conclusion differs.'
    }

    Invoke-Test 'requires the same matrix and current-main identity' {
        $manifestMismatch = New-ComparisonCase `
            -BaselineManifestSha 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
        Assert-Throws { Invoke-Comparison $manifestMismatch } 'different matrix manifests'

        $shaMismatch = New-ComparisonCase `
            -SelectedBaselineSha 'dddddddddddddddddddddddddddddddddddddddd'
        Assert-Throws { Invoke-Comparison $shaMismatch } 'does not match current main'
    }

    Invoke-Test 'reports missing and stale comparison conclusions' {
        $missingComparison = New-ComparisonCase -BaselineStatus 'missing'
        $missing = Invoke-Comparison $missingComparison
        Assert-Equal 'baseline-missing' $missing.conclusion 'Missing comparison conclusion differs.'

        $advancedMissingComparison = New-ComparisonCase -BaselineStatus 'missing'
        $advancedMissingComparison.ExpectedBaselineSha = 'dddddddddddddddddddddddddddddddddddddddd'
        $advancedMissing = Invoke-Comparison $advancedMissingComparison
        Assert-Equal 'baseline-missing' $advancedMissing.conclusion 'Advanced missing comparison conclusion differs.'
        Assert-Equal 'Baseline status is missing.' $advancedMissing.baseline.reason 'Advanced missing comparison reason differs.'

        $staleComparison = New-ComparisonCase -BaselineStatus 'available'
        $staleComparison.ExpectedBaselineSha = 'dddddddddddddddddddddddddddddddddddddddd'
        $stale = Invoke-Comparison $staleComparison
        Assert-Equal 'baseline-stale' $stale.conclusion 'Stale comparison conclusion differs.'

        $unavailableComparison = New-ComparisonCase
        $unavailable = Invoke-Comparison $unavailableComparison 'Current-main artifacts are unavailable.'
        Assert-Equal 'baseline-missing' $unavailable.conclusion 'Unavailable comparison conclusion differs.'
        Assert-Equal 'Current-main artifacts are unavailable.' $unavailable.baseline.reason 'Unavailable comparison reason differs.'
    }

    Invoke-Test 'aggregates coverage only in the trusted reporting workflow' {
        $ciWorkflow = Get-Content -Raw -LiteralPath $workflowPath
        $reportingWorkflow = Get-Content -Raw -LiteralPath $testResultsWorkflowPath
        Assert-Equal 0 ([regex]::Matches($ciWorkflow, 'dotnet-coverage merge')).Count 'Pull request code must not merge its own coverage.'
        Assert-Matches `
            $reportingWorkflow `
            '(?s)Checkout trusted reporter.*?Validate coverage matrix.*?Verify tested commit.*?Download covered source.*?Summarize coverage' `
            'Trusted reporting must validate raw reports against the covered source before summarizing.'
        Assert-Matches `
            $reportingWorkflow `
            '(?s)Summarize coverage.*?-ReportDirectory coverage-data' `
            'Canonical coverage must be summarized directly from the validated raw reports.'
        Assert-Matches `
            $reportingWorkflow `
            '(?s)Pin trusted main.*?TRUSTED_MAIN_SHA: \$\{\{ steps\.trusted-main\.outputs\.sha \}\}.*?-ExpectedSha \$env:TRUSTED_MAIN_SHA' `
            'Baseline selection must use the exact main snapshot executing the trusted reporter.'
        Assert-Matches `
            $reportingWorkflow `
            '(?s)Download covered source.*?Verify trusted coverage inputs.*?get-coverage-input-fingerprint\.ps1.*?Summarize coverage' `
            'Pull request coverage inputs must match the pinned trusted reporter before aggregation.'
        Assert-Equal 12 (@(Get-Content -LiteralPath $coverageInputsPath)).Count 'Trusted coverage input count differs.'
        Assert-Matches `
            $reportingWorkflow `
            'github\.event\.workflow_run\.conclusion == ''success''' `
            'Coverage reporting must require a successful complete CI run.'
        Assert-Matches `
            $reportingWorkflow `
            'conclusion: "success"' `
            'Coverage comparison must remain report-only during calibration.'
        Assert-Matches `
            $reportingWorkflow `
            'Compare coverage with current main' `
            'The coverage check must compare canonical coverage with current main.'
        Assert-Matches `
            $reportingWorkflow `
            '(?s)Select current-main baseline.*?Download current-main coverage.*?Validate current-main coverage matrix.*?Summarize current-main coverage.*?Compare coverage with current main' `
            'The trusted reporter must aggregate the same current-main matrix before comparison.'
        Assert-Matches `
            $reportingWorkflow `
            'ExpectedBaselineSha = \$currentMain\.object\.sha' `
            'The comparison must revalidate the current-main identity after aggregation.'
        Assert-Matches `
            $reportingWorkflow `
            '\[Uri\]::EscapeDataString\(\$env:DEFAULT_BRANCH\)' `
            'The current-main ref lookup must encode default branch names.'
    }

    Invoke-Test 'requires selected test jobs to upload coverage' {
        $archiveTestResultsAction = Get-Content -Raw -LiteralPath $archiveTestResultsActionPath
        $runTestsAction = Get-Content -Raw -LiteralPath $runTestsActionPath
        Assert-Matches `
            $archiveTestResultsAction `
            'if-no-files-found: error' `
            'Each selected pull request or current-main test job must publish coverage.'
        Assert-Matches `
            $archiveTestResultsAction `
            "inputs\.coverage == 'true'" `
            'Coverage uploads must be limited to selected test jobs.'
        Assert-Matches `
            $archiveTestResultsAction `
            "github\.event_name == 'push'.*?github\.event\.repository\.default_branch" `
            'Current-main test jobs must publish the same raw coverage artifacts.'
        Assert-Equal 1 ([regex]::Matches($archiveTestResultsAction, "github\.event_name == 'push' && 7 \|\| 1")).Count 'Current-main coverage retention count differs.'
        Assert-Matches `
            $archiveTestResultsAction `
            '(?s)path:\s*\|.*?TestResults/\$\{\{ inputs\.name \}\}\.cobertura\.xml.*?TestResults/\$\{\{ inputs\.name \}\}\.coverage\.json' `
            'Each selected test job must publish its exact coverage report.'
        Assert-Equal 2 ([regex]::Matches($archiveTestResultsAction, 'uses: actions/upload-artifact@')).Count 'Each artifact must have one immutable upload attempt.'
        Assert-Equal 0 ([regex]::Matches($archiveTestResultsAction, 'overwrite: true')).Count 'Artifact uploads must not replace an ambiguous partial upload.'
        Assert-Equal 1 ([regex]::Matches($archiveTestResultsAction, 'if-no-files-found: error')).Count 'Coverage upload must require the report.'
        Assert-Equal 1 ([regex]::Matches($archiveTestResultsAction, 'continue-on-error: true')).Count 'Only diagnostic upload may remain advisory.'
        Assert-Equal 1 ([regex]::Matches($archiveTestResultsAction, 'retention-days: \$\{\{ inputs\[''retention-days''\] \}\}')).Count 'Diagnostic upload must use the requested retention period.'
        Assert-Equal 1 ([regex]::Matches($runTestsAction, '(?m)^    id: retry\r?$')).Count 'The retry outcome must be available to diagnostic retention policy.'
        Assert-Equal 1 ([regex]::Matches($runTestsAction, 'retention-days: \$\{\{ steps\.test\.outcome == ''failure'' && steps\.retry\.outcome != ''success'' && 14 \|\| 1 \}\}')).Count 'Only unrecovered test failures may retain diagnostics for 14 days.'
        Assert-Matches `
            $archiveTestResultsAction `
            "(?ms)^  - id: archive-test-results\r?\n    uses: actions/upload-artifact@.*?\r?\n    continue-on-error: true\r?\n.*?^  - name: Report test result upload failure\r?\n    if: steps\.archive-test-results\.outcome == 'failure'\r?\n    shell: pwsh\r?\n    run: Write-Output '::warning title=Test result artifact upload failed::" `
            'Test result upload failures must remain advisory and explicit.'
        Assert-Matches `
            $archiveTestResultsAction `
            "(?ms)^  - name: Archive coverage\r?\n    if: .*?github\.event_name == 'pull_request'.*?github\.event_name == 'push'.*?\r?\n    uses: actions/upload-artifact@.*?\r?\n    with:\r?\n(?:(?!^  - ).)*?      if-no-files-found: error\r?$" `
            'Coverage upload must remain directly gating.'
    }

    Invoke-Test 'collects GitHub coverage only on Linux .NET 10' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        $runTestsAction = Get-Content -Raw -LiteralPath $runTestsActionPath
        $dotnetTestAction = Get-Content -Raw -LiteralPath $dotnetTestActionPath
        Assert-Equal 20 ([regex]::Matches($workflow, 'uses: \./\.github/actions/run-tests')).Count 'Test action count differs.'
        Assert-Equal 14 ([regex]::Matches($workflow, '(?m)^\s{8}provider: [A-Za-z]')).Count 'Provider-discovered test partition count differs.'
        Assert-Equal 2 ([regex]::Matches($runTestsAction, 'uses: \./\.github/actions/dotnet-test')).Count 'Native test action invocation count differs.'
        Assert-Equal 2 ([regex]::Matches($runTestsAction, "format\('/\[\(Provider=\{0\}\)")).Count 'Standard provider filter count differs.'
        $directTestCommands = ([regex]::Matches($dotnetTestAction, 'dotnet test --solution Orleans\.slnx')).Count
        $coveredTestCommands = ([regex]::Matches($dotnetTestAction, "(?s)'dotnet'\s*'test'\s*'--solution'\s*'Orleans\.slnx'")).Count
        Assert-Equal 2 ($directTestCommands + $coveredTestCommands) 'Native test command count differs.'
        Assert-Equal 4 ([regex]::Matches($runTestsAction, "runner\.os == 'Linux' && inputs\.framework == 'net10\.0'")).Count 'Coverage selection boundary count differs.'
        Assert-Equal 0 ([regex]::Matches($workflow + $runTestsAction + $dotnetTestAction, 'static-instrumentation|coverage\.static\.config\.xml|IncludeFiles')).Count 'GitHub coverage must not use static instrumentation.'
        Assert-Equal 1 ([regex]::Matches($workflow, "retry: 'true'")).Count 'Cosmos retry configuration count differs.'
        Assert-Matches $runTestsAction 'attempt1' 'The first retryable attempt must retain distinct test results.'
        Assert-Matches $runTestsAction 'attempt2' 'The second retryable attempt must retain distinct test results.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'test/.+\.(?:csproj|fsproj|dll)')).Count 'Workflow must not enumerate test projects or modules.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'run-.+tests?\.ps1')).Count 'Workflow must not invoke a PowerShell test runner.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'merge-coverage\.ps1')).Count 'Workflow must use native coverage merging.'
        Assert-Equal 0 ([regex]::Matches($dotnetTestAction, '--project|--test-modules')).Count 'Native test action must discover projects from the solution.'
    }

    Invoke-Test 'keeps Azure Pipelines tests outside coverage collection' {
        $azureBuildTemplate = Get-Content -Raw -LiteralPath $azureBuildTemplatePath
        $azureVariables = Get-Content -Raw -LiteralPath $azureVariablesPath
        Assert-Equal 0 ([regex]::Matches($azureVariables, 'DOTNET_COVERAGE_VERSION')).Count 'Azure Pipelines must not configure duplicate coverage collection.'
        Assert-Equal 0 ([regex]::Matches($azureBuildTemplate, 'setup-coverage\.ps1|invoke-coverage\.ps1|PublishCodeCoverage')).Count 'Azure Pipelines must not duplicate canonical GitHub coverage.'
        Assert-Matches `
            $azureBuildTemplate `
            '(?s)\$executable = \$command\[0\].*?\$arguments = \$command\[1\.\.\(\$command\.Count - 1\)\].*?& \$executable @arguments.*?exit \$LASTEXITCODE' `
            'Azure Pipelines must execute every test job directly and propagate its exit code.'
    }

    Invoke-Test 'validates the coverage tool version' {
        $setupCoverageScript = Get-Content -Raw -LiteralPath $setupCoverageScriptPath
        Assert-Matches `
            $setupCoverageScript `
            'DOTNET_COVERAGE_VERSION must specify' `
            'Coverage setup must reject a missing tool version.'
        Assert-Matches `
            $setupCoverageScript `
            'InstallPath must be specified outside GitHub Actions' `
            'Coverage setup must require an explicit installation path in other CI systems.'
        Assert-Matches `
            $setupCoverageScript `
            'if \(-not \[string\]::IsNullOrWhiteSpace\(\$env:GITHUB_PATH\)\)' `
            'Coverage setup must continue adding the tool to the GitHub Actions path.'
        Assert-Equal 2 ([regex]::Matches($setupCoverageScript, 'Assert-NotReparsePoint \$toolPath')).Count 'Coverage tool path validation count differs.'
    }

    $global:LASTEXITCODE = 0
    Write-Output "$testsRun coverage tests passed."
} finally {
    if (Test-Path $temporaryRoot) {
        Remove-Item -Recurse -Force $temporaryRoot
    }
}
