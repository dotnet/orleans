$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script = Join-Path $PSScriptRoot 'dissemination-performance.ps1'
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
if ($errors) {
    throw ($errors | Out-String)
}

$cases = @(
    @{ Name = 'minimum process count'; Parameter = 'Sizes'; Value = '2'; Error = '*distinct silo counts*' }
    @{ Name = 'maximum process count'; Parameter = 'Sizes'; Value = '129'; Error = '*distinct silo counts*' }
    @{ Name = 'duplicate sizes'; Parameter = 'Sizes'; Value = '4,4'; Error = '*distinct silo counts*' }
    @{ Name = 'size cardinality'; Parameter = 'Sizes'; Value = '3,4,5,6,7,8,9'; Error = '*cannot validate argument*' }
    @{ Name = 'minimum rounds'; Parameter = 'Iterations'; Value = 2; Error = '*cannot validate argument*' }
    @{ Name = 'maximum rounds'; Parameter = 'Iterations'; Value = 201; Error = '*cannot validate argument*' }
    @{ Name = 'maximum repetitions'; Parameter = 'Repetitions'; Value = 6; Error = '*cannot validate argument*' }
    @{ Name = 'duplicate scenarios'; Parameter = 'Scenarios'; Value = 'stable,stable'; Error = '*at most once*' }
    @{ Name = 'unknown scenario'; Parameter = 'Scenarios'; Value = 'unknown'; Error = '*cannot validate argument*' }
    @{ Name = 'absolute artifact path'; Parameter = 'ArtifactsDirectory'; Value = 'C:\outside'; Error = '*cannot validate argument*' }
    @{ Name = 'artifact traversal'; Parameter = 'ArtifactsDirectory'; Value = 'Artifacts\..\outside'; Error = '*cannot validate argument*' }
    @{ Name = 'artifact root'; Parameter = 'ArtifactsDirectory'; Value = 'Artifacts'; Error = '*cannot validate argument*' }
    @{ Name = 'nested artifact path'; Parameter = 'ArtifactsDirectory'; Value = 'Artifacts\run\bin'; Error = '*cannot validate argument*' }
    @{ Name = 'repository URL'; Parameter = 'CandidateRepository'; Value = 'https://github.com/ReubenBond/orleans'; Error = '*cannot validate argument*' }
    @{ Name = 'ref option'; Parameter = 'CandidateRef'; Value = '--upload-pack=command'; Error = '*cannot validate argument*' }
    @{ Name = 'processor bound'; Parameter = 'SiloProcessorCount'; Value = 65; Error = '*cannot validate argument*' }
    @{ Name = 'GC bound'; Parameter = 'GCConserveMemory'; Value = 10; Error = '*cannot validate argument*' }
)
foreach ($case in $cases) {
    $arguments = @{
        CandidateRepository = 'ReubenBond/orleans'
        CandidateRef = '3fc0a4610cd2eb5feef27e958fcca59658aaae47'
        SkipBuild = $true
    }
    $arguments[$case.Parameter] = $case.Value
    $rejected = $false
    try {
        & $script @arguments
    }
    catch {
        if ($_.Exception.Message -notlike $case.Error) {
            throw "Unexpected failure for $($case.Name): $_"
        }
        $rejected = $true
    }
    if (!$rejected) {
        throw "Expected rejection: $($case.Name)"
    }
}
Write-Output "Passed $($cases.Count) script-boundary checks."

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))
$artifactName = "DisseminationPathCheck-$([Guid]::NewGuid().ToString('N'))"
$testArtifacts = Join-Path $root 'Artifacts' $artifactName
$binaries = Join-Path $testArtifacts 'bin'
$runner = Join-Path $binaries 'Runner' 'Dissemination.PerformanceHarness.Tests.dll'
$calls = [System.Collections.Generic.List[object]]::new()
$environment = @{}
foreach ($entry in Get-ChildItem Env:ORLEANS_DISSEMINATION_*) {
    $environment[$entry.Name] = $entry.Value
}

function dotnet {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    if ($Arguments[0] -ne $runner) {
        throw "Unexpected runner path: $($Arguments[0])"
    }
    $calls.Add($Arguments)
    $global:LASTEXITCODE = 0
}

try {
    $selection = @{
        CandidateRepository = 'ReubenBond/orleans'
        CandidateRef = '3fc0a4610cd2eb5feef27e958fcca59658aaae47'
        CandidateCommit = '3fc0a4610cd2eb5feef27e958fcca59658aaae47'
        BaselineCommit = '6739589254b746a8790cf53524e6abe372bb53d4'
    }
    foreach ($runtime in @('Old', 'New')) {
        $directory = Join-Path $binaries $runtime
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        @{
            Runtime = $runtime
            SourceRevision = if ($runtime -eq 'Old') { $selection.BaselineCommit } else { $selection.CandidateCommit }
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'manifest.json')
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $runner) | Out-Null
    New-Item -ItemType File -Path $runner | Out-Null
    $selection | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $binaries 'selection.json')

    foreach ($separator in @('/', '\')) {
        & $script -CandidateRepository $selection.CandidateRepository -CandidateRef $selection.CandidateRef `
            -ArtifactsDirectory "Artifacts$separator$artifactName" -Sizes '3' -Iterations 3 -Repetitions 1 -SkipBuild
    }
    if ($calls.Count -ne 4) {
        throw "Expected measurement and scaling invocations for both path spellings; got $($calls.Count)."
    }
    for ($index = 0; $index -lt $calls.Count; $index++) {
        $expectedClass = if ($index % 2 -eq 0) { 'MeasurementTests' } else { 'ScalingTests' }
        if ($calls[$index][2] -ne "Orleans.Dissemination.PerformanceHarness.$expectedClass") {
            throw "Unexpected test selection in invocation $index."
        }
    }
    $runs = @(Get-ChildItem -LiteralPath (Join-Path $testArtifacts 'results') -Directory)
    if ($runs.Count -ne 2) {
        throw "Expected two results directories under the same artifact root; got $($runs.Count)."
    }
    foreach ($run in $runs) {
        foreach ($filename in @('invocation.json', 'selection.json', 'methodology.json', 'host-resources.json')) {
            $null = Get-Content -LiteralPath (Join-Path $run.FullName $filename) -Raw | ConvertFrom-Json
        }
        $invocation = Get-Content -LiteralPath (Join-Path $run.FullName 'invocation.json') -Raw | ConvertFrom-Json
        if ($invocation.ResultsDirectory -ne $run.FullName) {
            throw 'The recorded results path differs from the created directory.'
        }
    }
    Write-Output 'Passed 2 SkipBuild path checks with worker execution stubbed.'
}
finally {
    foreach ($entry in Get-ChildItem Env:ORLEANS_DISSEMINATION_*) {
        [Environment]::SetEnvironmentVariable($entry.Name, $null)
    }
    foreach ($entry in $environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    if (Test-Path -LiteralPath $testArtifacts) {
        Remove-Item -LiteralPath $testArtifacts -Recurse -Force
    }
}
