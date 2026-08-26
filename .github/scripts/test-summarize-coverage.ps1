[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'summarize-coverage.ps1'
$coverageReportScriptPath = Join-Path $PSScriptRoot 'coverage-report.ps1'
$archiveTestResultsActionPath = Join-Path $PSScriptRoot '../actions/archive-test-results/action.yml'
$dotnetTestActionPath = Join-Path $PSScriptRoot '../actions/dotnet-test/action.yml'
$invokeCoverageScriptPath = Join-Path $PSScriptRoot 'invoke-coverage.ps1'
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
        $coverageReportScript = Get-Content -Raw -LiteralPath $coverageReportScriptPath
        Assert-Matches `
            $coverageReportScript `
            '"\$CoverageId\.cobertura\.xml"' `
            'Coverage file names must include the complete matrix identity.'
    }

    Invoke-Test 'uses external coverage collection for CI builds' {
        $dotnetTestAction = Get-Content -Raw -LiteralPath $dotnetTestActionPath
        $coverageReportScript = Get-Content -Raw -LiteralPath $coverageReportScriptPath
        Assert-Matches `
            $dotnetTestAction `
            'invoke-coverage\.ps1' `
            'Coverage must use the external collector with ContinuousIntegrationBuild.'
        Assert-Matches `
            $dotnetTestAction `
            '-p:ContinuousIntegrationBuild=false' `
            'Coverage builds must disable deterministic CI instrumentation.'
        Assert-Matches `
            $dotnetTestAction `
            '-IncludeFiles' `
            'macOS coverage must specify files for static instrumentation.'
        Assert-Matches `
            $dotnetTestAction `
            'coverage\.static\.config\.xml' `
            'macOS coverage must use static-only instrumentation settings.'
        Assert-Matches `
            $dotnetTestAction `
            '--no-build' `
            'Coverage tests must execute the statically instrumented build.'
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
    }

    Invoke-Test 'retries only the uninitialized coverage handle failure' {
        $testCase = New-TestCase
        $attemptFile = Join-Path $testCase.Root 'attempt.txt'
        $fakeCollector = Join-Path $testCase.Root 'fake-collector.ps1'
        $collectorArguments = Join-Path $testCase.Root 'collector-arguments'
        $settings = Join-Path $testCase.Root 'coverage.config.xml'
        $coverageOutput = Join-Path $testCase.ReportDirectory 'coverage.cobertura.xml'
        $retryLog = Join-Path $testCase.Root 'logs/coverage.retry.log'
        [IO.File]::WriteAllText($settings, '<Configuration />', [Text.UTF8Encoding]::new($false))
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
                -Command @('dotnet', 'test', '-p:ContinuousIntegrationBuild=false')
            Assert-Equal 0 $LASTEXITCODE 'The retry should succeed.'
            Assert-Equal 2 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'The collector attempt count differs.'
            $retryArguments = Get-Content -LiteralPath "$collectorArguments.2.txt"
            Assert-Equal $true ($retryArguments -contains '-p:ContinuousIntegrationBuild=false') 'The inner MSBuild property must be forwarded.'
            Assert-Equal $true ($retryArguments -contains '--log-file') 'The retry must capture a collector log.'
            Assert-Equal $true ($retryArguments -contains 'Verbose') 'The retry collector log must be verbose.'

            Remove-Item -LiteralPath $attemptFile
            $env:ORLEANS_COVERAGE_FAILURE = 'test'
            & $invokeCoverageScriptPath `
                -Settings $settings `
                -Output $coverageOutput `
                -RetryLogFile $retryLog `
                -CoverageCommand $fakeCollector `
                -Command @('dotnet', 'test', '-p:ContinuousIntegrationBuild=false')
            Assert-Equal 1 $LASTEXITCODE 'An unrelated test failure should be preserved.'
            Assert-Equal 1 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'An unrelated failure must not be retried.'

            Remove-Item -LiteralPath $attemptFile
            $env:ORLEANS_COVERAGE_FAILURE = 'handle-always'
            & $invokeCoverageScriptPath `
                -Settings $settings `
                -Output $coverageOutput `
                -RetryLogFile $retryLog `
                -CoverageCommand $fakeCollector `
                -Command @('dotnet', 'test', '-p:ContinuousIntegrationBuild=false')
            Assert-Equal 1 $LASTEXITCODE 'A persistent collector failure should be preserved.'
            Assert-Equal 2 ([int] (Get-Content -Raw -LiteralPath $attemptFile)) 'The collector must retry only once.'
        } finally {
            $env:ORLEANS_COVERAGE_ATTEMPT_FILE = $previousAttemptFile
            $env:ORLEANS_COVERAGE_ARGUMENTS = $previousArguments
            $env:ORLEANS_COVERAGE_FAILURE = $previousFailure
        }
    }

    Invoke-Test 'downloads only coverage artifacts for merging' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        Assert-Matches `
            $workflow `
            '(?s)pattern:\s*coverage_test_output_\*.*?merge-multiple:\s*true' `
            'Coverage reports must download directly into the merge directory.'
    }

    Invoke-Test 'requires every test matrix job before merging' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        $archiveTestResultsAction = Get-Content -Raw -LiteralPath $archiveTestResultsActionPath
        Assert-Matches `
            $workflow `
            "if: github\.event_name == 'pull_request' && needs\.ci\.result == 'success'" `
            'Coverage merge must run only after every CI job succeeds.'
        Assert-Matches `
            $workflow `
            'needs: ci' `
            'Coverage merge must depend on the aggregate CI job.'
        Assert-Matches `
            $workflow `
            '(?s)coverage-merge:.*?actions/checkout@.*?actions/setup-dotnet@.*?actions/setup-coverage' `
            'Coverage merge must check out scripts before running local actions.'
        Assert-Matches `
            $workflow `
            'dotnet-coverage merge "coverage-data/\*\.cobertura\.xml"' `
            'Coverage reports must be merged directly by dotnet-coverage.'
        Assert-Matches `
            $workflow `
            'New-Item -ItemType Directory -Force TestResults' `
            'Coverage merge must create its output directory.'
        Assert-Matches `
            $archiveTestResultsAction `
            'if-no-files-found: error' `
            'Each successful pull request test job must publish coverage.'
        Assert-Matches `
            $archiveTestResultsAction `
            'path: TestResults/\$\{\{ inputs\.name \}\}\.cobertura\.xml' `
            'Each test job must publish its exact coverage report.'
    }

    Invoke-Test 'collects coverage from every test job' {
        $workflow = Get-Content -Raw -LiteralPath $workflowPath
        $runTestsAction = Get-Content -Raw -LiteralPath $runTestsActionPath
        $dotnetTestAction = Get-Content -Raw -LiteralPath $dotnetTestActionPath
        Assert-Equal 18 ([regex]::Matches($workflow, 'uses: \./\.github/actions/run-tests')).Count 'Test action count differs.'
        Assert-Equal 16 ([regex]::Matches($workflow, '(?m)^\s{8}provider: [A-Za-z]')).Count 'Provider-discovered test partition count differs.'
        Assert-Equal 2 ([regex]::Matches($runTestsAction, 'uses: \./\.github/actions/dotnet-test')).Count 'Native test action invocation count differs.'
        Assert-Equal 2 ([regex]::Matches($runTestsAction, "format\('/\[\(Provider=\{0\}\)")).Count 'Standard provider filter count differs.'
        $directTestCommands = ([regex]::Matches($dotnetTestAction, 'dotnet test --solution Orleans\.slnx')).Count
        $coveredTestCommands = ([regex]::Matches($dotnetTestAction, "(?s)'dotnet'\s*'test'\s*'--solution'\s*'Orleans\.slnx'")).Count
        Assert-Equal 4 ($directTestCommands + $coveredTestCommands) 'Native test command count differs.'
        Assert-Matches $dotnetTestAction '--framework "\$\{\{ inputs\.framework \}\}".*?--list-tests' 'Static coverage builds must target and discover the selected framework.'
        Assert-Equal 1 ([regex]::Matches($workflow, "retry: 'true'")).Count 'Cosmos retry configuration count differs.'
        Assert-Matches $runTestsAction 'attempt1' 'The first retryable attempt must retain distinct test results.'
        Assert-Matches $runTestsAction 'attempt2' 'The second retryable attempt must retain distinct test results.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'test/.+\.(?:csproj|fsproj|dll)')).Count 'Workflow must not enumerate test projects or modules.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'run-.+tests?\.ps1')).Count 'Workflow must not invoke a PowerShell test runner.'
        Assert-Equal 0 ([regex]::Matches($workflow, 'merge-coverage\.ps1')).Count 'Workflow must use native coverage merging.'
        Assert-Equal 0 ([regex]::Matches($dotnetTestAction, '--project|--test-modules')).Count 'Native test action must discover projects from the solution.'
    }

    Invoke-Test 'validates the coverage tool version' {
        $setupCoverageScript = Get-Content -Raw -LiteralPath $setupCoverageScriptPath
        Assert-Matches `
            $setupCoverageScript `
            'DOTNET_COVERAGE_VERSION must specify' `
            'Coverage setup must reject a missing tool version.'
        Assert-Equal 2 ([regex]::Matches($setupCoverageScript, 'Assert-NotReparsePoint \$toolPath')).Count 'Coverage tool path validation count differs.'
    }

    $global:LASTEXITCODE = 0
    Write-Output "$testsRun coverage tests passed."
} finally {
    if (Test-Path $temporaryRoot) {
        Remove-Item -Recurse -Force $temporaryRoot
    }
}
