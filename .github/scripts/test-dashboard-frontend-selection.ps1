[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$selector = Join-Path $PSScriptRoot 'select-dashboard-frontend-check.ps1'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans frontend selection $([guid]::NewGuid())"
$fixture = Join-Path $temporaryRoot 'repository'
$eventPath = Join-Path $temporaryRoot 'event.json'
$testsRun = 0

function Invoke-Git {
    param([string[]] $Arguments, [string] $Root = $fixture, [string] $InputText = '')
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Git $Arguments failed: $($stderr.GetAwaiter().GetResult())" }
        return $stdout.GetAwaiter().GetResult().Trim()
    } finally {
        $process.Dispose()
    }
}

function New-Commit {
    param([string[]] $Parents, [string[]] $Add = @(), [string[]] $Remove = @(), [string] $Tree = $baseTree)
    [void] (Invoke-Git @('read-tree', $Tree))
    $updates = @(
        foreach ($path in $Remove) { "0 $('0' * 40)`t$path`0" }
        foreach ($path in $Add) { "100644 $blob`t$path`0" }
    )
    if ($updates.Count) { [void] (Invoke-Git @('update-index', '-z', '--index-info') -InputText ($updates -join '')) }
    $treeSha = Invoke-Git @('write-tree')
    $arguments = @('commit-tree', $treeSha, '-m', 'fixture')
    foreach ($parent in $Parents) { $arguments += @('-p', $parent) }
    return Invoke-Git $arguments
}

function New-Pr {
    param([string[]] $Add = @(), [string[]] $Remove = @(), [string] $Parent = $base)
    $head = New-Commit -Parents $Parent -Add $Add -Remove $Remove
    $merge = New-Commit -Parents @($base, $head) -Tree (Invoke-Git @('rev-parse', "$head^{tree}"))
    [void] (Invoke-Git @('update-ref', 'refs/heads/main', $merge))
    [IO.File]::WriteAllText($eventPath, (@{
        pull_request = @{ base = @{ sha = $base }; head = @{ sha = $head } }
    } | ConvertTo-Json -Depth 4))
    return $merge
}

function Assert-Selection {
    param(
        [string] $Sha, [bool] $Required, [string] $Reason = '',
        [string] $Root = $fixture, [string] $Event = 'pull_request',
        [string] $Payload = $eventPath
    )
    $outputPath = Join-Path $temporaryRoot 'github-output.txt'
    [IO.File]::WriteAllText($outputPath, '')
    $selection = & $selector -Repository $Root -EventName $Event -EventPath $Payload -TestedSha $Sha -GitHubOutput $outputPath 6>$null
    if ($selection.required -ne $Required -or ($Reason -and $selection.reason -notlike "*$Reason*")) {
        throw "Expected required=$Required reason containing '$Reason', actual $($selection | ConvertTo-Json -Depth 4 -Compress)"
    }
    if ([IO.File]::ReadAllText($outputPath).Trim() -cne "required=$($Required.ToString().ToLowerInvariant())") {
        throw 'GitHub output must carry exactly the boolean selection.'
    }
    return $selection
}

function Test-Case {
    param([string] $Name, [scriptblock] $Action)
    & $Action
    $script:testsRun++
    Write-Output "PASS $Name"
}

try {
    [void] [IO.Directory]::CreateDirectory($fixture)
    [void] (Invoke-Git @('init', '--initial-branch=main'))
    [void] (Invoke-Git @('config', 'user.name', 'Frontend selector fixture'))
    [void] (Invoke-Git @('config', 'user.email', 'fixture@example.invalid'))
    [void] (Invoke-Git @('config', 'commit.gpgsign', 'false'))
    # Object-only fixtures exercise Git's Linux paths without checking them out on Windows.
    [void] (Invoke-Git @('config', 'core.protectNTFS', 'false'))
    $blob = 'fixture content' | & git -C $fixture hash-object -w --stdin
    if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture blob.' }
    [void] (Invoke-Git @('update-index', '--add', '--cacheinfo', "100644,$blob,src/Dashboard/Orleans.Dashboard.App/src/removed.ts"))
    $baseTree = Invoke-Git @('write-tree')
    $base = Invoke-Git @('commit-tree', $baseTree, '-m', 'base')
    [void] (Invoke-Git @('update-ref', 'refs/heads/main', $base))

    Test-Case 'BackendOnlyPrSkipsReproduction' {
        $sha = New-Pr -Add 'src/Orleans.Runtime/Catalog/ActivationData.cs'
        $selection = Assert-Selection $sha $false 'complete immutable'
        if ($selection.changed_paths.Count -ne 1 -or $selection.comparison_base -ne $base) {
            throw 'The selection must expose the complete tested/base comparison.'
        }
    }

    Test-Case 'EveryManifestInputAndOutputSelectsReproduction' {
        $app = 'src/Dashboard/Orleans.Dashboard.App/'
        $paths = @(
            foreach ($name in 'assets.inputs.sha256', 'assets.outputs.sha256') {
                foreach ($line in [IO.File]::ReadAllLines((Join-Path $repository "${app}build/$name"))) {
                    if ($line.Length -lt 67) { throw "Invalid manifest entry in $name." }
                    $prefix = if ($name -eq 'assets.outputs.sha256') { 'dist/' } else { '' }
                    "$app$prefix$($line.Substring(66))"
                }
            }
        )
        $selection = Assert-Selection (New-Pr -Add $paths) $true
        if ((($selection.affected_paths | Sort-Object -CaseSensitive) -join "`0") -cne (($paths | Sort-Object -CaseSensitive) -join "`0")) {
            throw 'Every manifest input/output path must independently be classified as affected.'
        }
    }

    Test-Case 'IntegrationPolicyAndNewInputsSelectReproduction' {
        $paths = @(
            'src/Dashboard/Orleans.Dashboard.App/build/assets.inputs.sha256',
            'src/Dashboard/Orleans.Dashboard.App/build/assets.outputs.sha256',
            'src/Dashboard/Orleans.Dashboard.App/.env.production',
            'src/Dashboard/Orleans.Dashboard.App/new-build.config',
            'src/Dashboard/Orleans.Dashboard.App/src/new-component.tsx',
            'src/Dashboard/Orleans.Dashboard.App/public/new-asset.bin',
            'src/Dashboard/Orleans.Dashboard.App/dist/new-output.js',
            'src/Dashboard/Orleans.Dashboard/Orleans.Dashboard.csproj',
            'src/Dashboard/Orleans.Dashboard.App/build/Orleans.Dashboard.Frontend.targets',
            'src/Dashboard/Orleans.Dashboard.App/build/VerifyDashboardFrontendAssets.cs',
            'src/Dashboard/Orleans.Dashboard.App/build/new-task.cs',
            '.github/workflows/ci.yml', '.github/workflows/new-bootstrap.yml',
            '.github/actions/cache-dependencies/action.yml',
            '.github/actions/new-bootstrap/action.yml',
            '.github/scripts/select-dashboard-frontend-check.ps1',
            '.github/scripts/test-dashboard-frontend-selection.ps1',
            'src/Dashboard/Orleans.Dashboard.App/scripts/install.mjs',
            'src/Dashboard/Orleans.Dashboard.App/scripts/new-helper.mjs',
            'eng/common.ps1',
            '.azure/pipelines/templates/build.yaml',
            '.gitattributes', 'global.json', 'Directory.Build.props',
            'Directory.Build.targets', 'Directory.Packages.props',
            'src/Directory.Build.props', 'src/Directory.Build.targets',
            'Orleans.slnx', 'build.ps1', 'Build.cmd', 'common.ps1', 'NuGet.Config'
        )
        $selection = Assert-Selection (New-Pr -Add $paths) $true
        if ((($selection.affected_paths | Sort-Object -CaseSensitive) -join "`0") -cne (($paths | Sort-Object -CaseSensitive) -join "`0")) {
            throw 'Every integration/policy/new-input path must independently be classified as affected.'
        }
    }

    Test-Case 'ForgedOutputAndManifestPairSelectsReproduction' {
        $sha = New-Pr -Add @('src/Dashboard/Orleans.Dashboard.App/dist/index.min.js', 'src/Dashboard/Orleans.Dashboard.App/build/assets.outputs.sha256')
        $selection = Assert-Selection $sha $true
        if ($selection.affected_paths.Count -ne 2) { throw 'Both forged paths must select independent reproduction.' }
    }

    Test-Case 'ReadmeScreenshotsAndBrowserTestsSkipReproduction' {
        $sha = New-Pr -Add @(
            'README.md', 'docs/site/src/content/docs/dashboard.md',
            'src/Dashboard/Orleans.Dashboard.App/README.md',
            'src/Dashboard/Orleans.Dashboard.App/screenshots/dashboard.png',
            'src/Dashboard/Orleans.Dashboard.App/tests/navigation.spec.ts',
            'src/Dashboard/Orleans.Dashboard/README.md'
        )
        [void] (Assert-Selection $sha $false)
    }

    Test-Case 'EarlierFrontendCommitSurvivesLaterBackendCommit' {
        $early = New-Commit -Parents $base -Add 'src/Dashboard/Orleans.Dashboard.App/src/early.ts'
        $head = New-Commit -Parents $early -Tree (Invoke-Git @('rev-parse', "$early^{tree}")) -Add 'src/Orleans.Runtime/backend.cs'
        $merge = New-Commit -Parents @($base, $head) -Tree (Invoke-Git @('rev-parse', "$head^{tree}"))
        [void] (Invoke-Git @('update-ref', 'refs/heads/main', $merge))
        [IO.File]::WriteAllText($eventPath, (@{ pull_request = @{ base = @{ sha = $base }; head = @{ sha = $head } } } | ConvertTo-Json -Depth 4))
        $selection = Assert-Selection $merge $true
        if ($selection.changed_paths.Count -ne 2) { throw 'Selection examined only the last PR commit.' }
    }

    Test-Case 'DeletionAndRenameOutOfAffectedScopeSelectReproduction' {
        $removed = 'src/Dashboard/Orleans.Dashboard.App/src/removed.ts'
        $selection = Assert-Selection (New-Pr -Remove $removed) $true
        if ($selection.affected_paths -cnotcontains $removed) { throw 'Deletion was missed.' }
        $selection = Assert-Selection (New-Pr -Remove $removed -Add 'docs/renamed.ts') $true
        if ($selection.changed_paths -cnotcontains $removed -or $selection.changed_paths -cnotcontains 'docs/renamed.ts') {
            throw 'The old and new rename paths must both be inspected.'
        }
        [void] (Assert-Selection (New-Pr -Add 'src/Dashboard/Orleans.Dashboard.App/src/REMOVED.ts' -Remove $removed) $true)
    }

    Test-Case 'RenameIntoAffectedScopeSelectsNewPath' {
        $outside = New-Commit -Parents $base -Add 'docs/renamed.ts'
        $oldBase = $base
        $oldTree = $baseTree
        try {
            $base = $outside
            $baseTree = Invoke-Git @('rev-parse', "$base^{tree}")
            $selection = Assert-Selection (New-Pr -Remove 'docs/renamed.ts' -Add 'src/Dashboard/Orleans.Dashboard.App/src/renamed.ts') $true
            if ($selection.changed_paths.Count -ne 2 -or $selection.affected_paths -cnotcontains 'src/Dashboard/Orleans.Dashboard.App/src/renamed.ts') {
                throw 'Rename into the frontend boundary must inspect the new path.'
            }
        } finally {
            $base = $oldBase
            $baseTree = $oldTree
        }
    }

    Test-Case 'EmptyProvenPrSkipsReproduction' {
        [void] (Assert-Selection (New-Pr) $false)
    }

    Test-Case 'NulDelimitedPathsAndLargeDiffAreComplete' {
        $paths = @(1..3501 | ForEach-Object { "src/Orleans.Runtime/backend-$_.cs" }) +
            @("src/Dashboard/Orleans.Dashboard.App/public/space and`ttab`nnewline.bin")
        $selection = Assert-Selection (New-Pr -Add $paths) $true
        if ($selection.changed_paths.Count -ne $paths.Count -or $selection.affected_paths.Count -ne 1) {
            throw 'Git paths were truncated, quoted, or split on whitespace.'
        }
    }

    Test-Case 'DepthTwoProvesWholePrAndDepthOneRunsConservatively' {
        $sha = New-Pr -Add 'src/Orleans.Runtime/backend.cs'
        foreach ($depth in 1, 2) {
            $clone = Join-Path $temporaryRoot "depth-$depth"
            [void] (Invoke-Git @('clone', '--quiet', '--no-checkout', "--depth=$depth", ([Uri]::new("$fixture$([IO.Path]::DirectorySeparatorChar)")).AbsoluteUri, $clone))
            $reason = if ($depth -eq 1) { 'tree comparison is unavailable' } else { 'complete immutable' }
            [void] (Assert-Selection $sha ($depth -eq 1) -Root $clone -Reason $reason)
        }
    }

    Test-Case 'UnknownNonPrAndQueueEventsAlwaysRun' {
        $sha = New-Pr -Add 'src/Orleans.Runtime/backend.cs'
        foreach ($event in @('push', 'workflow_dispatch', 'merge_group', 'pull_request_target', 'unknown', '')) {
            [void] (Assert-Selection $sha $true -Event $event -Payload '')
        }
    }

    Test-Case 'MissingAndInconsistentCommitIdentityRunsConservatively' {
        $sha = New-Pr -Add 'src/Orleans.Runtime/backend.cs'
        [void] (Assert-Selection $sha $true -Payload (Join-Path $temporaryRoot 'missing.json'))
        [void] (Assert-Selection '' $true)
        [void] (Assert-Selection ('f' * 40) $true)
        [IO.File]::WriteAllText($eventPath, '{}')
        [void] (Assert-Selection $sha $true)
        [IO.File]::WriteAllText($eventPath, (@{ pull_request = @{ base = @{ sha = 'f' * 40 }; head = @{ sha = $base } } } | ConvertTo-Json -Depth 4))
        [void] (Assert-Selection $sha $true)
        [void] (Invoke-Git @('update-ref', 'refs/heads/main', $base))
        [void] (Assert-Selection $base $true)
    }

    Test-Case 'MalformedEventFailsSelectionRatherThanSkipping' {
        [IO.File]::WriteAllText($eventPath, '{invalid')
        $failed = $false
        try { [void] (& $selector -Repository $fixture -EventName pull_request -EventPath $eventPath -TestedSha $base -GitHubOutput '') }
        catch [ArgumentException] { $failed = $true }
        if (-not $failed) { throw 'Invalid event JSON must fail selection.' }
    }

    Test-Case 'WorkflowKeepsRequiredBuildAndSdkGatesActive' {
        $workflow = [IO.File]::ReadAllText((Join-Path $repository '.github/workflows/ci.yml'))
        $build = [regex]::Match($workflow, '(?ms)^  build:\r?\n.*?(?=^  [\w-]+:|\z)').Value
        if (-not $build -or $build -match '(?m)^    if:|^    needs:') { throw 'Existing Build job must always run.' }
        if ($workflow -match '(?m)^    paths(-ignore)?:' -or $build -notmatch 'os: \[ubuntu-latest, windows-latest\]') {
            throw 'The existing workflow triggers and required OS matrix must remain active.'
        }
        if ($build -notmatch 'fetch-depth: 2' -or $build -notmatch 'id: dashboard-assets' -or $build -notmatch 'select-dashboard-frontend-check\.ps1') {
            throw 'Selection must execute on the depth-two checkout before toolchain setup.'
        }
        $select = [regex]::Match($build, '(?ms)^    - name: Select Dashboard asset reproduction\r?\n.*?(?=^    - |\z)').Value
        if ($select -match '(?m)^      if:|continue-on-error:' -or $build.IndexOf('Select Dashboard asset reproduction') -ge $build.IndexOf('Setup frontend generation toolchain')) {
            throw 'Selection must be an unconditional failing step before Node setup.'
        }
        foreach ($step in @('Setup frontend generation toolchain', 'Check committed Dashboard assets', 'Test Dashboard npm recovery')) {
            $body = [regex]::Match($build, "(?ms)^    - name: $step\r?\n.*?(?=^    - |\z)").Value
            if ($body -notmatch "if: steps\.dashboard-assets\.outputs\.required != 'false'" -or $body -match 'continue-on-error:') {
                throw "$step must fail closed with the selector boolean and propagate failures."
            }
        }
        $cache = [regex]::Match($build, '(?ms)^    - name: Cache dependencies\r?\n.*?(?=^    - |\z)').Value
        if (-not $cache.Contains('npm: ${{ steps.dashboard-assets.outputs.required != ''false'' }}') -or $cache -match '(?m)^      if:|nuget:') {
            throw 'Only the npm portion of the combined cache may be conditional.'
        }
        foreach ($step in @('Setup .NET', 'Test Dashboard asset validation', 'Test Dashboard reproduction selection', 'Build', 'Check CodeGen graph inventory')) {
            $body = [regex]::Match($build, "(?ms)^    - name: $([regex]::Escape($step))\r?\n.*?(?=^    - |\z)").Value
            if (-not $body -or $body -match '(?m)^      if:') { throw "$step must remain unconditional." }
        }
        if ($build -match 'test-dependency-cache|test-versioned-grain-build') {
            throw 'Withdrawn cache/deployment harness gates must remain absent.'
        }
        foreach ($step in @('Test coverage reporting', 'Pack and validate package compatibility', 'Test package consumers')) {
            $body = [regex]::Match($build, "(?ms)^    - name: $step\r?\n.*?(?=^    - |\z)").Value
            if ($body -notmatch "if: matrix\.os == 'ubuntu-latest'" -or $body -match 'dashboard-assets\.outputs|continue-on-error:') {
                throw "$step must retain its independent Linux gate."
            }
        }
        $action = [IO.File]::ReadAllText((Join-Path $repository '.github/actions/cache-dependencies/action.yml'))
        foreach ($step in @('Locate npm store', 'Cache npm downloads')) {
            $body = [regex]::Match($action, "(?ms)^  - name: $step\r?\n.*?(?=^  - |\z)").Value
            if ($body -notmatch "if: inputs\.npm == 'true'") { throw "$step must honor the npm-only input." }
        }
        foreach ($step in @('Locate NuGet store', 'Cache NuGet packages')) {
            $body = [regex]::Match($action, "(?ms)^  - name: $step\r?\n.*?(?=^  - |\z)").Value
            if ($body -notmatch "if: inputs\.nuget == 'true'" -or $body -match 'inputs\.npm') { throw "$step must remain independent of npm selection." }
        }
        $ui = [regex]::Match($workflow, '(?ms)^  dashboard-ui:\r?\n.*?(?=^  [\w-]+:|\z)').Value
        if ($ui -match 'dashboard-assets.outputs' -or $ui -notmatch 'npm run test:ui') { throw 'UI regression behavior must remain independent.' }
        $targets = [IO.File]::ReadAllText((Join-Path $repository 'src/Dashboard/Orleans.Dashboard.App/build/Orleans.Dashboard.Frontend.targets'))
        if ($targets -notmatch 'BeforeTargets="BeforeBuild;GenerateNuspec"' -or $targets -match 'dashboard-assets|required|GITHUB_EVENT') {
            throw 'SDK build/pack verification must stay independent of CI selection.'
        }
        $trustedInputs = [IO.File]::ReadAllLines((Join-Path $repository '.github/coverage-inputs.txt'))
        foreach ($input in @('.github/actions/cache-dependencies/action.yml', '.github/solutions/CodeGenerator.slnf', '.github/scripts/select-dashboard-frontend-check.ps1')) {
            if ($trustedInputs -cnotcontains $input) { throw "Missing coverage trust input: $input" }
        }
    }

    Write-Output "Passed $testsRun Dashboard frontend selection tests."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
