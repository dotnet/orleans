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
