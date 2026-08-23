[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'summarize-coverage.ps1'
$collectorScriptPath = Join-Path $PSScriptRoot 'run-dotnet-test.ps1'
$codeGeneratorScriptPath = Join-Path $PSScriptRoot 'run-codegenerator-tests.ps1'
$cosmosScriptPath = Join-Path $PSScriptRoot 'run-cosmos-tests.ps1'
$mergeScriptPath = Join-Path $PSScriptRoot 'merge-coverage.ps1'
$runTestProjectsScriptPath = Join-Path $PSScriptRoot 'run-test-projects.ps1'
$runTestsActionPath = Join-Path $PSScriptRoot '../actions/run-tests/action.yml'
$setupCoverageScriptPath = Join-Path $PSScriptRoot 'setup-coverage.ps1'
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
        <class filename="$encodedSourceFile">
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

    Invoke-Test 'counts distinct source files' {
        $testCase = New-TestCase
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

    Invoke-Test 'rejects multiple reports' {
        $testCase = New-TestCase
        $xml = Get-ReportXml (Join-Path $testCase.SourceRoot 'Example.cs')
        [void] (Write-Report $testCase $xml 'first.cobertura.xml')
        [void] (Write-Report $testCase $xml 'second.cobertura.xml')
        Assert-Throws { Invoke-Summarizer $testCase } 'Expected one merged Cobertura report'
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
        Assert-Throws { Invoke-Summarizer $testCase } 'no measured lines'
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
        $collectorScript = Get-Content -Raw -LiteralPath $collectorScriptPath
        Assert-Matches `
            $collectorScript `
            '"\$CoverageId\.cobertura\.xml"' `
            'Coverage file names must include the complete matrix identity.'
    }

    Invoke-Test 'uses external coverage collection for CI builds' {
        $collectorScript = Get-Content -Raw -LiteralPath $collectorScriptPath
        $codeGeneratorScript = Get-Content -Raw -LiteralPath $codeGeneratorScriptPath
        Assert-Matches `
            $collectorScript `
            '& \$coverageTool @coverageArguments @testArguments' `
            'Coverage must use the external collector with ContinuousIntegrationBuild.'
        Assert-Matches `
            $collectorScript `
            '-p:ContinuousIntegrationBuild=false' `
            'Coverage builds must disable deterministic CI instrumentation.'
        Assert-Matches `
            $collectorScript `
            '\$staticInstrumentationFiles = Join-Path' `
            'macOS coverage must specify files for static instrumentation.'
        Assert-Matches `
            $collectorScript `
            'coverage\.static\.config\.xml' `
            'macOS coverage must use static-only instrumentation settings.'
        Assert-Matches `
            $codeGeneratorScript `
            '(?s)-UseStaticInstrumentation.*?-UseStaticInstrumentation' `
            'Both CodeGen runs must use static instrumentation.'
        Assert-Matches `
            $collectorScript `
            '\$testArguments\.Add\(''--no-build''\)' `
            'Coverage tests must execute the statically instrumented build.'
        Assert-Matches `
            $collectorScript `
            'Assert-NotReparsePoint \$coverageDirectory' `
            'Coverage collection must reject a linked output directory.'
        Assert-Matches `
            $collectorScript `
            'Assert-NotReparsePoint \$coverageOutput' `
            'Coverage collection must reject a linked output file.'
        Assert-Matches `
            $collectorScript `
            'contains no measured lines' `
            'Coverage collection must reject empty reports from successful test runs.'
        Assert-Matches `
            $collectorScript `
            'Push-Location \$testWorkingDirectory' `
            'Prebuilt test modules must execute from their output directory.'
    }

    Invoke-Test 'preserves coverage artifact directories during download' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        Assert-Matches `
            $workflow `
            '(?s)pattern:\s*test_output_\*.*?merge-multiple:\s*false' `
            'Coverage artifacts must not be flattened before merging.'
    }

    Invoke-Test 'requires every test matrix job before merging' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        $mergeScript = Get-Content -Raw -LiteralPath $mergeScriptPath
        Assert-Matches `
            $workflow `
            "if: github\.event_name == 'pull_request' && !cancelled\(\)" `
            'Coverage merge must run after test failures.'
        Assert-Matches `
            $workflow `
            'needs: ci' `
            'Coverage merge must depend on the aggregate CI job.'
        Assert-Matches `
            $workflow `
            '(?s)coverage-merge:.*?actions/checkout@.*?actions/setup-dotnet@.*?actions/setup-coverage' `
            'Coverage merge must check out scripts before running local actions.'
        Assert-Matches `
            $mergeScript `
            'ExpectedArtifactCount' `
            'The merge must validate every test matrix artifact.'
        Assert-Matches `
            $mergeScript `
            'contains no coverage report' `
            'The merge must reject test artifacts without coverage.'
    }

    Invoke-Test 'collects coverage from every test job' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        $runTestsAction = Get-Content -Raw -LiteralPath $runTestsActionPath
        $runTestProjectsScript = Get-Content -Raw -LiteralPath $runTestProjectsScriptPath
        $codeGeneratorScript = Get-Content -Raw -LiteralPath $codeGeneratorScriptPath
        $cosmosScript = Get-Content -Raw -LiteralPath $cosmosScriptPath
        Assert-Equal 15 ([regex]::Matches($workflow, 'uses: \./\.github/actions/run-tests')).Count 'Standard test action count differs.'
        Assert-Equal 3 ([regex]::Matches($workflow, 'uses: \./\.github/actions/setup-test-environment')).Count 'Special test setup count differs.'
        Assert-Equal 1 ([regex]::Matches($runTestsAction, 'run-test-projects\.ps1')).Count 'Standard test action command count differs.'
        Assert-Equal 1 ([regex]::Matches($runTestProjectsScript, 'run-dotnet-test\.ps1')).Count 'Standard test command count differs.'
        Assert-Equal 2 ([regex]::Matches($codeGeneratorScript, 'run-dotnet-test\.ps1')).Count 'CodeGen test command count differs.'
        Assert-Equal 1 ([regex]::Matches($cosmosScript, 'run-dotnet-test\.ps1')).Count 'Cosmos test command count differs.'
        Assert-Equal 1 ([regex]::Matches($workflow, 'run-dotnet-test\.ps1')).Count 'Core matrix test command count differs.'
        Assert-Equal 6 ([regex]::Matches($workflow, 'uses: \./\.github/actions/restore-provider-test-assets')).Count 'Shared provider build consumer count differs.'
    }

    Invoke-Test 'validates the coverage tool version' {
        $setupCoverageScript = Get-Content -Raw -LiteralPath $setupCoverageScriptPath
        Assert-Matches `
            $setupCoverageScript `
            'DOTNET_COVERAGE_VERSION must specify' `
            'Coverage setup must reject a missing tool version.'
        Assert-Equal 2 ([regex]::Matches($setupCoverageScript, 'Assert-NotReparsePoint \$toolPath')).Count 'Coverage tool path validation count differs.'
    }

    Write-Output "$testsRun coverage tests passed."
} finally {
    if (Test-Path $temporaryRoot) {
        Remove-Item -Recurse -Force $temporaryRoot
    }
}
