$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script = Join-Path $PSScriptRoot 'dissemination-performance.ps1'
$errors = $null
$scriptAst = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
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
    @{ Name = 'unknown workload'; Parameter = 'Workload'; Value = 'unknown'; Error = '*cannot validate argument*' }
    @{ Name = 'open-loop stable only'; Parameter = 'Workload'; Value = 'OpenLoopSynchronized'; Error = 'Open-loop workloads require*' }
    @{ Name = 'open-loop duration bound'; Parameter = 'Iterations'; Value = 31; Additional = @{ Workload = 'OpenLoopStaggered'; Scenarios = 'stable' }; Error = 'Open-loop workloads require*' }
    @{ Name = 'open-loop process bound'; Parameter = 'Sizes'; Value = '4,33'; Additional = @{ Workload = 'OpenLoopSynchronized'; Scenarios = 'stable' }; Error = 'Open-loop workloads require*' }
)
foreach ($case in $cases) {
    $arguments = @{
        CandidateRepository = 'ReubenBond/orleans'
        CandidateRef = '3fc0a4610cd2eb5feef27e958fcca59658aaae47'
        SkipBuild = $true
    }
    $arguments[$case.Parameter] = $case.Value
    if ($case.ContainsKey('Additional')) {
        foreach ($entry in $case.Additional.GetEnumerator()) {
            $arguments[$entry.Key] = $entry.Value
        }
    }
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
    & {
        foreach ($name in @('Assert-NoLinks', 'Copy-Worker')) {
            $definition = $scriptAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $false)
            . ([scriptblock]::Create($definition.Extent.Text))
        }

        Assert-NoLinks $root
        Assert-NoLinks (Join-Path $testArtifacts 'not-created')
        foreach ($outside in @((Split-Path -Parent $root), [System.IO.Path]::GetPathRoot($root), "$root-sibling")) {
            $rejected = $false
            try {
                Assert-NoLinks $outside
            }
            catch {
                if ($_.Exception.Message -notlike 'Path is outside the repository boundary:*') {
                    throw
                }
                $rejected = $true
            }
            if (!$rejected) {
                throw "Expected repository-boundary rejection for $outside."
            }
        }
        Write-Output 'Passed 5 repository-boundary checks.'

        $harnessSource = Join-Path $root 'test' 'Dissemination.PerformanceHarness'
        $checkout = Join-Path $testArtifacts 'runtime-checkout'
        $existingHarness = Join-Path $checkout 'test' 'Dissemination.IntegrationHarness'
        New-Item -ItemType Directory -Path $existingHarness -Force | Out-Null
        $sentinel = Join-Path $existingHarness 'preserved.txt'
        Set-Content -LiteralPath $sentinel -Value 'Existing candidate harness remains intact.'
        Copy-Worker $checkout
        $copied = Join-Path $checkout 'test' 'Dissemination.PerformanceHarness'
        if (@(Get-ChildItem -LiteralPath $copied -Directory).Count -ne 2) {
            throw 'Copy-Worker must copy exactly the Silo and Shared directories.'
        }
        foreach ($part in @('Silo', 'Shared')) {
            $expected = @(Get-ChildItem -LiteralPath (Join-Path $harnessSource $part) -File |
                Where-Object { $_.Extension -in '.cs', '.csproj' })
            $actual = @(Get-ChildItem -LiteralPath (Join-Path $copied $part) -File)
            if ($expected.Count -ne $actual.Count) {
                throw "Copied $part inventory differs from the tooling source."
            }
            foreach ($file in $expected) {
                $copy = Join-Path $copied $part $file.Name
                if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
                    throw "Copied file differs: $copy"
                }
            }
        }
        $rejected = $false
        try {
            Copy-Worker $checkout
        }
        catch {
            if ($_.Exception.Message -notlike 'Refusing to overwrite an existing runtime harness:*') {
                throw
            }
            $rejected = $true
        }
        if (!$rejected -or (Get-Content -LiteralPath $sentinel) -ne 'Existing candidate harness remains intact.') {
            throw 'Copy-Worker must preserve both existing harness directories.'
        }
        Write-Output 'Passed worker-copy inventory, coexistence and overwrite checks.'

        foreach ($linkedPart in @('test', 'harness')) {
            foreach ($dangling in @($false, $true)) {
                $caseName = "$linkedPart-$dangling"
                $linkedCheckout = Join-Path $testArtifacts "linked-checkout-$caseName"
                $target = Join-Path $testArtifacts "outside-checkout-$caseName"
                New-Item -ItemType Directory -Path $linkedCheckout, $target | Out-Null
                $link = Join-Path $linkedCheckout 'test'
                if ($linkedPart -eq 'harness') {
                    New-Item -ItemType Directory -Path $link | Out-Null
                    $link = Join-Path $link 'Dissemination.PerformanceHarness'
                }
                $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
                New-Item -ItemType $linkType -Path $link -Target $target | Out-Null
                try {
                    if ($dangling) {
                        [System.IO.Directory]::Delete($target)
                    }
                    else {
                        Set-Content -LiteralPath (Join-Path $target 'preserved.txt') -Value 'Untouched link target.'
                    }
                    $rejected = $false
                    try {
                        Copy-Worker $linkedCheckout
                    }
                    catch {
                        if ($_.Exception.Message -ne "Artifact and runtime paths must use ordinary directories: $link") {
                            throw
                        }
                        $rejected = $true
                    }
                    if (!$rejected) {
                        throw "Copy-Worker accepted a linked destination parent: $caseName"
                    }
                    if ($dangling) {
                        if (Test-Path -LiteralPath $target) {
                            throw "Copy-Worker created the dangling link target: $caseName"
                        }
                    }
                    else {
                        $files = @(Get-ChildItem -LiteralPath $target -Force)
                        if ($files.Count -ne 1 -or $files[0].Name -ne 'preserved.txt' -or
                            (Get-Content -LiteralPath $files[0].FullName) -ne 'Untouched link target.') {
                            throw "Copy-Worker modified the link target: $caseName"
                        }
                    }
                }
                finally {
                    Remove-Item -LiteralPath $link -Force
                }
            }
        }
        Write-Output 'Passed 4 worker-copy linked-parent checks with untouched targets.'
    }

    $methodologyPath = Join-Path $root 'test' 'Dissemination.PerformanceHarness' 'methodology.json'
    $methodology = Get-Content -LiteralPath $methodologyPath -Raw | ConvertFrom-Json
    $scriptText = Get-Content -LiteralPath $script -Raw
    $baselineMatch = [regex]::Match($scriptText, '(?m)^\$baselineSha = ''([0-9a-f]{40})''\r?$')
    if (!$baselineMatch.Success -or $baselineMatch.Groups[1].Value -ne $methodology.baseline.commit) {
        throw 'Script and methodology baseline pins differ.'
    }
    $baselineSha = $baselineMatch.Groups[1].Value
    $controller = Get-Content -LiteralPath (Join-Path $root 'test' 'Dissemination.PerformanceHarness' 'Tests' 'ProcessCluster.cs') -Raw
    $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' 'workflows' 'dissemination-performance.yml') -Raw
    $readme = Get-Content -LiteralPath (Join-Path $root 'test' 'Dissemination.PerformanceHarness' 'README.md') -Raw
    if (!$controller.Contains("public const string Baseline = `"$baselineSha`";") -or
        $workflow -notmatch "repository: dotnet/orleans\s+ref: $baselineSha\b" -or !$readme.Contains($baselineSha)) {
        throw 'Controller, workflow and documentation must retain the same pinned original runtime.'
    }
    Write-Output 'Passed baseline-pin consistency checks.'

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
    foreach ($workload in @('OpenLoopSynchronized', 'OpenLoopStaggered')) {
        & $script -CandidateRepository $selection.CandidateRepository -CandidateRef $selection.CandidateRef `
            -ArtifactsDirectory (Join-Path 'Artifacts' $artifactName) -Sizes '3' -Iterations 30 -Repetitions 1 `
            -Scenarios stable -Workload $workload -SkipBuild
        if ($env:ORLEANS_DISSEMINATION_WORKLOAD -ne $workload -or $env:ORLEANS_DISSEMINATION_ITERATIONS -ne '30') {
            throw "Open-loop invocation lost its workload selection: $workload"
        }
    }
    if ($calls.Count -ne 8) {
        throw "Expected measurement and scaling invocations for two path spellings and two open-loop patterns; got $($calls.Count)."
    }
    for ($index = 0; $index -lt $calls.Count; $index++) {
        $expectedClass = if ($index % 2 -eq 0) { 'MeasurementTests' } else { 'ScalingTests' }
        if ($calls[$index][2] -ne "Orleans.Dissemination.PerformanceHarness.$expectedClass") {
            throw "Unexpected test selection in invocation $index."
        }
    }
    $runs = @(Get-ChildItem -LiteralPath (Join-Path $testArtifacts 'results') -Directory)
    if ($runs.Count -ne 4) {
        throw "Expected four results directories under the same artifact root; got $($runs.Count)."
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
    Write-Output 'Passed 4 SkipBuild path/workload checks with worker execution stubbed.'
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
