[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$downloadScript = Join-Path $PSScriptRoot 'download-test-results.ps1'
$publishScript = Join-Path $PSScriptRoot 'publish-test-results.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans-test-results-tests-$([guid]::NewGuid())"
$testsRun = 0
$savedToken = $env:GH_TOKEN
$savedSummary = $env:GITHUB_STEP_SUMMARY
$sha = '0123456789abcdef0123456789abcdef01234567'

function Assert-Equal {
    param($Expected, $Actual)
    if ($Expected -cne $Actual) {
        throw "Expected '$Expected', actual '$Actual'."
    }
}

function Assert-Matches {
    param([string] $Value, [string] $Pattern)
    if ($Value -notmatch $Pattern) {
        throw "Expected content matching '$Pattern', actual '$Value'."
    }
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $Pattern)
    try {
        & $Action
    } catch {
        Assert-Matches $_.Exception.Message $Pattern
        return
    }
    throw "Expected error matching '$Pattern'."
}

function New-Artifact {
    param(
        [long] $Id,
        [string] $Name = 'test_output_Functional_windows_net10.0',
        [string] $CreatedAt = '2026-09-18T20:14:10Z',
        [string] $Outcome = 'Passed',
        [string] $EntryName = 'TestResults/result.trx'
    )

    $archivePath = Join-Path $state.Root "$Id.zip"
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry($EntryName)
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write(@"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testId="$Id" testName="Test$Id" outcome="$Outcome" /></Results>
</TestRun>
"@)
        } finally {
            $writer.Dispose()
        }
    } finally {
        $archive.Dispose()
    }
    $state.Archives[$Id] = $archivePath
    return [pscustomobject]@{
        id = $Id
        name = $Name
        created_at = $CreatedAt
        expired = $false
        digest = "sha256:$((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant())"
        workflow_run = @{ id = 42; head_sha = $sha }
    }
}

function Set-Artifacts {
    param([object[]] $Artifacts)
    $state.Pages = @(@{ total_count = $Artifacts.Count; artifacts = $Artifacts })
}

function gh {
    $global:LASTEXITCODE = 0
    Assert-Equal 'api' $args[0]
    switch ($args[1]) {
        'repos/dotnet/orleans/actions/runs/42' {
            $state.RunReads++
            $attempt = if ($state.RunReads -gt 1) { $state.FinalAttempt } else { $state.InitialAttempt }
            return (@{ id = 42; head_sha = $sha; run_attempt = $attempt; status = 'completed' } | ConvertTo-Json)
        }
        'repos/dotnet/orleans/actions/runs/42/artifacts?per_page=100' {
            Assert-Equal $true ($args -contains '--paginate')
            Assert-Equal $true ($args -contains '--slurp')
            $state.ListReads++
            $global:LASTEXITCODE = $state.ListExitCode
            return (ConvertTo-Json -InputObject $state.Pages -Depth 10)
        }
        default { throw "Unexpected GitHub API call: $args" }
    }
}

function Invoke-WebRequest {
    param([string] $Uri, [hashtable] $Headers, [string] $OutFile)
    $match = [regex]::Match($Uri, '^https://api.github.com/repos/dotnet/orleans/actions/artifacts/([0-9]+)/zip$')
    Assert-Equal $true $match.Success
    $id = [long] $match.Groups[1].Value
    Assert-Equal 'Bearer test-token' $Headers.Authorization
    $state.Downloads.Add($id)
    if ($state.DownloadError) {
        throw $state.DownloadError
    }
    Copy-Item -LiteralPath $state.Archives[$id] -Destination $OutFile
}

function Invoke-Download {
    & $downloadScript -ResultsPath $state.Results -Repository dotnet/orleans -RunId 42 -RunAttempt 2 -Sha $sha
}

function Get-Report {
    return ((& $publishScript -ResultsPath $state.Results -Repository dotnet/orleans -Sha $sha -DryRun) -join "`n")
}

function Test-Case {
    param([string] $Name, [scriptblock] $Action)
    $caseRoot = Join-Path $temporaryRoot $Name
    [void] (New-Item -ItemType Directory -Path $caseRoot)
    $script:state = @{
        Root = $caseRoot
        Results = Join-Path $caseRoot 'results'
        Archives = @{}
        Downloads = [Collections.Generic.List[long]]::new()
        DownloadError = ''
        InitialAttempt = 2
        FinalAttempt = 2
        RunReads = 0
        ListReads = 0
        ListExitCode = 0
        Pages = @()
    }
    & $Action
    $script:testsRun++
    Write-Output "PASS $Name"
}

try {
    $env:GH_TOKEN = 'test-token'
    $env:GITHUB_STEP_SUMMARY = $null

    Test-Case 'NewerLowerIdReplacesStaleFailure' {
        $old = New-Artifact -Id 10566260879 -Outcome Failed -EntryName 'TestResults/old.trx'
        $new = New-Artifact -Id 10566159940 -CreatedAt '2026-09-18T20:38:46Z'
        Set-Artifacts @($old, $new)
        Invoke-Download
        Assert-Equal '10566159940' ($state.Downloads -join ',')
        Assert-Equal $false (Test-Path (Join-Path $state.Results "$($old.name)/TestResults/old.trx"))
        Assert-Matches (Get-Report) 'Conclusion: success'
        Assert-Matches (Get-Report) '\| 1 \| 0 \| 0 \| 1 \| 1 \|'
    }

    Test-Case 'DuplicateNamesAreOrderIndependent' {
        $old = New-Artifact -Id 99
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z'
        foreach ($artifacts in @(@($old, $new), @($new, $old))) {
            $state.Results = Join-Path $state.Root ([guid]::NewGuid())
            Set-Artifacts $artifacts
            Invoke-Download
        }
        Assert-Equal '10,10' ($state.Downloads -join ',')
    }

    Test-Case 'FailedOnlyRerunRetainsUntouchedPartitions' {
        $old = New-Artifact -Id 99 -Outcome Failed
        $retained = New-Artifact -Id 98 -Name 'test_output_BVT_linux_net8.0'
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z'
        Set-Artifacts @($old, $retained, $new)
        Invoke-Download
        Assert-Equal '98,10' ($state.Downloads -join ',')
        $report = Get-Report
        Assert-Matches $report '\| BVT_linux_net8.0 \| 1 \| 1 \| 0 \| 0 \|'
        Assert-Matches $report '\| Functional_windows_net10.0 \| 1 \| 1 \| 0 \| 0 \|'
        Assert-Matches $report '\| 2 \| 0 \| 0 \| 2 \| 2 \|'
        Assert-Matches $report 'Conclusion: success'
    }

    Test-Case 'AllPagesParticipateInSelection' {
        $firstPage = @(New-Artifact -Id 199 -Outcome Failed)
        $firstPage += @(1..99 | ForEach-Object { @{ id = $_; name = "build_log_$_" } })
        $new = New-Artifact -Id 110 -CreatedAt '2026-09-18T20:38:46Z'
        $state.Pages = @(
            @{ total_count = 101; artifacts = $firstPage },
            @{ total_count = 101; artifacts = @($new) }
        )
        Invoke-Download
        Assert-Equal 1 $state.ListReads
        Assert-Equal '110' ($state.Downloads -join ',')
        Assert-Matches (Get-Report) 'Conclusion: success'
    }

    Test-Case 'GenuineLatestFailureIsReported' {
        $old = New-Artifact -Id 99
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z' -Outcome Failed
        Set-Artifacts @($old, $new)
        Invoke-Download
        Assert-Equal '10' ($state.Downloads -join ',')
        $report = Get-Report
        Assert-Matches $report 'Conclusion: failure'
        Assert-Matches $report 'Test10 \[Failed\]'
        Assert-Matches $report '\| 0 \| 1 \| 0 \| 1 \| 1 \|'
    }

    Test-Case 'AmbiguousLatestTimesFail' {
        Set-Artifacts @((New-Artifact -Id 99), (New-Artifact -Id 10))
        Assert-Throws { Invoke-Download } 'ambiguous latest creation times'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'EquivalentOffsetsAreAmbiguous' {
        Set-Artifacts @(
            (New-Artifact -Id 99),
            (New-Artifact -Id 10 -CreatedAt '2026-09-18T13:14:10-07:00')
        )
        Assert-Throws { Invoke-Download } 'ambiguous latest creation times'
    }

    Test-Case 'MissingOrInvalidCreationTimeFails' {
        $artifact = New-Artifact -Id 10
        foreach ($value in @($null, 'invalid')) {
            $artifact.created_at = $value
            Set-Artifacts @($artifact)
            Assert-Throws { Invoke-Download } 'invalid created_at'
        }
    }

    Test-Case 'ExpiredLatestDoesNotFallBack' {
        $old = New-Artifact -Id 99
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z'
        $new.expired = $true
        Set-Artifacts @($old, $new)
        Assert-Throws { Invoke-Download } 'Latest test result artifact.*expired'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'ExpiredSupersededArtifactIsIgnored' {
        $old = New-Artifact -Id 99
        $old.expired = $true
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z'
        Set-Artifacts @($old, $new)
        Invoke-Download
        Assert-Equal '10' ($state.Downloads -join ',')
    }

    Test-Case 'UnavailableSelectedArtifactFails' {
        $old = New-Artifact -Id 99
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z'
        Set-Artifacts @($old, $new)
        foreach ($status in @(404, 410)) {
            $state.Results = Join-Path $state.Root "$status"
            $state.DownloadError = "HTTP $status"
            Assert-Throws { Invoke-Download } "HTTP $status"
        }
        Assert-Equal '10,10' ($state.Downloads -join ',')
    }

    Test-Case 'IncompleteOrChangingListingFails' {
        $artifact = New-Artifact -Id 10
        $state.Pages = @(@{ total_count = 2; artifacts = @($artifact) })
        Assert-Throws { Invoke-Download } 'changed or is incomplete'
        $state.Pages += @{ total_count = 1; artifacts = @($artifact) }
        Assert-Throws { Invoke-Download } 'changed or is incomplete'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'RepeatedIdsAcrossPagesFail' {
        $artifact = New-Artifact -Id 10
        $state.Pages = @(
            @{ total_count = 2; artifacts = @($artifact) },
            @{ total_count = 2; artifacts = @($artifact) }
        )
        Assert-Throws { Invoke-Download } 'invalid or repeated id'
    }

    Test-Case 'ListingFailureStopsDownload' {
        Set-Artifacts @((New-Artifact -Id 10))
        $state.ListExitCode = 1
        Assert-Throws { Invoke-Download } 'Unable to read GitHub API endpoint'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'NoMatchingArtifactsFail' {
        Set-Artifacts @()
        Assert-Throws { Invoke-Download } 'No test result artifacts'
        Set-Artifacts @(@{ id = 1; name = 'coverage_test_output_Functional' })
        Assert-Throws { Invoke-Download } 'No test result artifacts'
    }

    Test-Case 'InvalidArtifactProvenanceFails' {
        $artifact = New-Artifact -Id 10
        foreach ($provenance in @(
            @{ id = 41; head_sha = $sha },
            @{ id = 42; head_sha = ('a' * 40) },
            $null
        )) {
            $artifact.workflow_run = $provenance
            Set-Artifacts @($artifact)
            Assert-Throws { Invoke-Download } 'does not belong to run 42'
        }
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'SupersededRunAttemptFails' {
        Set-Artifacts @((New-Artifact -Id 10))
        $state.InitialAttempt = 3
        Assert-Throws { Invoke-Download } 'not completed attempt 2'
        Assert-Equal 0 $state.ListReads
    }

    Test-Case 'RunAttemptChangingDuringDownloadFails' {
        Set-Artifacts @((New-Artifact -Id 10))
        $state.FinalAttempt = 3
        Assert-Throws { Invoke-Download } 'not completed attempt 2'
        Assert-Equal 2 $state.RunReads
    }

    Test-Case 'DigestMismatchFails' {
        $artifact = New-Artifact -Id 10
        $artifact.digest = 'sha256:' + ('0' * 64)
        Set-Artifacts @($artifact)
        Assert-Throws { Invoke-Download } 'failed SHA-256 digest validation'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $state.Results -Recurse -File).Count
    }

    Test-Case 'OptionalApiDigestIsSupported' {
        $artifact = New-Artifact -Id 10
        $artifact.PSObject.Properties.Remove('digest')
        Set-Artifacts @($artifact)
        Invoke-Download
        Assert-Matches (Get-Report) 'Conclusion: success'
    }

    Test-Case 'EmptyLatestResultsFail' {
        $old = New-Artifact -Id 99
        $new = New-Artifact -Id 10 -CreatedAt '2026-09-18T20:38:46Z' -EntryName 'logs/output.log'
        Set-Artifacts @($old, $new)
        Assert-Throws { Invoke-Download } 'contains no TRX files'
        Assert-Equal '10' ($state.Downloads -join ',')
    }

    Test-Case 'ExistingResultsDirectoryFails' {
        Set-Artifacts @((New-Artifact -Id 10))
        [void] (New-Item -ItemType Directory -Path $state.Results)
        Assert-Throws { Invoke-Download } 'already exists'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'InvalidArtifactNameFails' {
        Set-Artifacts @((New-Artifact -Id 10 -Name 'test_output_../../outside'))
        Assert-Throws { Invoke-Download } 'Invalid test result artifact name'
        Assert-Equal 0 $state.Downloads.Count
    }

    Test-Case 'ArchiveTraversalFails' {
        Set-Artifacts @((New-Artifact -Id 10 -EntryName '../outside.trx'))
        Assert-Throws { Invoke-Download } 'outside the specified destination'
        Assert-Equal $false (Test-Path -LiteralPath (Join-Path $state.Results 'outside.trx'))
    }

    Write-Output "$testsRun test result tests passed."
} finally {
    $env:GH_TOKEN = $savedToken
    $env:GITHUB_STEP_SUMMARY = $savedSummary
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
