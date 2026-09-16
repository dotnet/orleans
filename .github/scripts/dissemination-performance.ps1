[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $CandidateRepository,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$')]
    [string] $CandidateRef,
    [ValidatePattern('^Artifacts[\\/][A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string] $ArtifactsDirectory = (Join-Path 'Artifacts' 'DisseminationPerformance'),
    [ValidatePattern('^\d+(,\d+){0,5}$')]
    [string] $Sizes = '4,8',
    [ValidateRange(3, 200)]
    [int] $Iterations = 3,
    [ValidateRange(1, 5)]
    [int] $Repetitions = 2,
    [ValidateSet('All', 'Current')]
    [string] $RuntimePaths = 'All',
    [ValidatePattern('^(stable|churn|partition)(,(stable|churn|partition)){0,2}$')]
    [string] $Scenarios = 'stable,churn,partition',
    [ValidateRange(0, 64)]
    [int] $SiloProcessorCount = 0,
    [ValidateRange(0, 9)]
    [int] $GCConserveMemory = 0,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$baselineSha = '6739589254b746a8790cf53524e6abe372bb53d4'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))
$harnessSource = Join-Path $root 'test' 'Dissemination.PerformanceHarness'
$sizesArray = @($Sizes.Split(',') | ForEach-Object { [int]$_ })
if (@($sizesArray | Select-Object -Unique).Count -ne $sizesArray.Count -or
    @($sizesArray | Where-Object { $_ -lt 3 -or $_ -gt 128 }).Count -gt 0) {
    throw 'Select one to six distinct silo counts between 3 and 128.'
}
if (@($Scenarios.Split(',') | Select-Object -Unique).Count -ne $Scenarios.Split(',').Count) {
    throw 'Select each scenario at most once.'
}

function Assert-NoLinks([string] $Path) {
    $current = [System.IO.Path]::GetFullPath($Path)
    $relative = [System.IO.Path]::GetRelativePath($root, $current)
    if ([System.IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or
        $relative.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw "Path is outside the repository boundary: $Path"
    }
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    while (![string]::Equals($current, $root, $comparison)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Artifact and runtime paths must use ordinary directories: $current"
            }
        }
        $parent = Split-Path -Parent $current
        if (!$parent -or [string]::Equals($parent, $current, $comparison)) {
            throw "Path is outside the repository boundary: $Path"
        }
        $current = $parent
    }
}

function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')"
    }
}

function Get-Revision([string] $Directory) {
    $revision = & git -C $Directory rev-parse HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Not a source checkout: $Directory"
    }
    return $revision.Trim()
}

function Assert-Checkout([string] $Directory, [string] $Repository) {
    Assert-NoLinks $Directory
    $topLevel = & git -C $Directory rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [System.IO.Path]::GetFullPath($topLevel.Trim()) -ne $Directory) {
        throw "Runtime must have its own isolated checkout: $Directory"
    }
    $url = & git -C $Directory remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $url.Trim().TrimEnd('/') -notin @("https://github.com/$Repository", "https://github.com/$Repository.git")) {
        throw "Runtime checkout must originate from https://github.com/$Repository."
    }
    $changes = & git -C $Directory status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $changes) {
        throw "Selected runtime checkout must be pristine before copying worker support: $Directory"
    }
}

function Copy-Worker([string] $Checkout) {
    $destination = Join-Path $Checkout 'test' 'Dissemination.PerformanceHarness'
    Assert-NoLinks $destination
    if (Test-Path -LiteralPath $destination) {
        throw "Refusing to overwrite an existing runtime harness: $destination"
    }
    foreach ($part in @('Silo', 'Shared')) {
        $partDestination = Join-Path $destination $part
        New-Item -ItemType Directory -Path $partDestination -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $harnessSource $part) -File |
            Where-Object { $_.Extension -in '.cs', '.csproj' } |
            Copy-Item -Destination $partDestination
    }
}

function Publish-Silo([string] $Checkout, [string] $Runtime, [string] $Revision) {
    $project = Join-Path $Checkout 'test' 'Dissemination.PerformanceHarness' 'Silo' 'Dissemination.Silo.csproj'
    $output = Join-Path $binaries $Runtime
    Push-Location $Checkout
    try {
        Invoke-DotNet @(
            'publish', $project, '--configuration', 'Release', '--framework', 'net10.0',
            '--output', $output, '--verbosity', 'minimal',
            "-p:HarnessRuntime=$Runtime", "-p:SourceRevisionId=$Revision",
            '-p:OfficialBuild=true', "-p:BUILD_SOURCEVERSION=$Revision",
            "-bl:$(Join-Path $artifacts "build-$Runtime.binlog")"
        )
    }
    finally {
        Pop-Location
    }
    $assemblyPath = Join-Path $output 'Orleans.Runtime.dll'
    $identity = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
    $assemblies = @{}
    foreach ($file in Get-ChildItem -LiteralPath $output -Filter 'Orleans*.dll' -File) {
        $name = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        $assemblies[$name.Name] = @{
            AssemblyVersion = $name.Version.ToString()
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    @{
        Runtime = $Runtime
        SourceRevision = $Revision
        AssemblyName = $identity.Name
        AssemblyVersion = $identity.Version.ToString()
        Sha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
        Assemblies = $assemblies
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json')
}

$artifactName = ($ArtifactsDirectory -split '[\\/]')[1]
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root 'Artifacts' $artifactName))
Assert-NoLinks $artifacts
if (Test-Path -LiteralPath $artifacts) {
    foreach ($item in Get-ChildItem -LiteralPath $artifacts -Recurse -Force) {
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Artifact contents must use ordinary files and directories: $($item.FullName)"
        }
    }
}
$binaries = Join-Path $artifacts 'bin'
if (!$SkipBuild) {
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw 'Runtime builds run in CI. Download the binary artifact and use -SkipBuild for local reproduction.'
    }
    if (Test-Path -LiteralPath $binaries) {
        throw "Use a fresh artifact directory for a new build: $binaries"
    }
    $baseline = Join-Path $root '.dissemination-runtime-original'
    $candidate = Join-Path $root '.dissemination-runtime-candidate'
    Assert-Checkout $baseline 'dotnet/orleans'
    Assert-Checkout $candidate $CandidateRepository
    if ((Get-Revision $baseline) -ne $baselineSha) {
        throw "Original runtime must be pinned to $baselineSha."
    }
    $candidateSha = Get-Revision $candidate
    if ($CandidateRef -match '^[0-9a-fA-F]{40}$' -and $candidateSha -ne $CandidateRef) {
        throw "Candidate checkout does not match requested commit $CandidateRef."
    }
    if (!(Test-Path -LiteralPath (Join-Path $candidate 'src' 'Orleans.Runtime' 'Dissemination'))) {
        throw 'Select a candidate revision containing the dissemination runtime APIs.'
    }
    if (Test-Path -LiteralPath (Join-Path $baseline 'src' 'Orleans.Runtime' 'Dissemination')) {
        throw 'Original runtime unexpectedly contains dissemination.'
    }
    New-Item -ItemType Directory -Force $binaries | Out-Null
    Copy-Worker $baseline
    Copy-Worker $candidate
    Publish-Silo $baseline 'Old' $baselineSha
    Publish-Silo $candidate 'New' $candidateSha
    Invoke-DotNet @(
        'publish', (Join-Path $harnessSource 'Tests' 'Dissemination.PerformanceHarness.Tests.csproj'),
        '--configuration', 'Release', '--framework', 'net10.0',
        '--output', (Join-Path $binaries 'Runner'), '--verbosity', 'minimal',
        "-bl:$(Join-Path $artifacts 'build-runner.binlog')"
    )
    @{
        ToolingCommit = Get-Revision $root
        BaselineRepository = 'dotnet/orleans'
        BaselineCommit = $baselineSha
        CandidateRepository = $CandidateRepository
        CandidateRef = $CandidateRef
        CandidateCommit = $candidateSha
        DotNetInfo = (& dotnet --info | Out-String)
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $binaries 'selection.json')
}

$selection = Get-Content -LiteralPath (Join-Path $binaries 'selection.json') -Raw | ConvertFrom-Json
if ($selection.CandidateRepository -ne $CandidateRepository -or $selection.CandidateRef -cne $CandidateRef -or
    $selection.BaselineCommit -ne $baselineSha) {
    throw 'Requested revisions must match the downloaded binary selection.json.'
}
foreach ($runtime in @('Old', 'New')) {
    $manifest = Get-Content -LiteralPath (Join-Path $binaries $runtime 'manifest.json') -Raw | ConvertFrom-Json
    $expected = if ($runtime -eq 'Old') { $baselineSha } else { $selection.CandidateCommit }
    if ($manifest.SourceRevision -ne $expected -or $manifest.Runtime -ne $runtime) {
        throw "Binary manifest does not match selected $runtime revision."
    }
}

$runner = Join-Path $binaries 'Runner' 'Dissemination.PerformanceHarness.Tests.dll'
if (!(Test-Path -LiteralPath $runner)) {
    throw "Published runner not found: $runner"
}
$results = Join-Path $artifacts 'results' "$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $results | Out-Null
Copy-Item -LiteralPath (Join-Path $harnessSource 'methodology.json') -Destination $results
Copy-Item -LiteralPath (Join-Path $binaries 'selection.json') -Destination $results

$largestSize = ($sizesArray | Measure-Object -Maximum).Maximum
$requiredBytes = [long]$largestSize * 128MB + 1GB
if ($IsLinux) {
    $memory = Get-Content -LiteralPath '/proc/meminfo'
    $availableLine = $memory | Where-Object { $_ -match '^MemAvailable:\s+(\d+)\s+kB$' }
    if (!$availableLine -or $availableLine -notmatch '^MemAvailable:\s+(\d+)\s+kB$') {
        throw 'Cannot establish available host memory.'
    }
    $availableBytes = [long]$Matches[1] * 1024
}
elseif ($IsWindows) {
    $availableBytes = [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
}
else {
    throw 'Resource preflight supports Linux CI and Windows/Linux local reproduction.'
}
@{
    AvailableBytes = $availableBytes
    RequiredPlanningBytes = $requiredBytes
    PlanningBytesPerSilo = 128MB
    ControllerHeadroomBytes = 1GB
    HostProcessorCount = [Environment]::ProcessorCount
    SiloProcessorCount = $SiloProcessorCount
    GCConserveMemory = $GCConserveMemory
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'host-resources.json')
if ($availableBytes -lt $requiredBytes) {
    throw "Insufficient memory for $largestSize silos: available=$availableBytes; planning requirement=$requiredBytes."
}

$env:ORLEANS_DISSEMINATION_OLD = Join-Path $binaries 'Old'
$env:ORLEANS_DISSEMINATION_NEW = Join-Path $binaries 'New'
$env:ORLEANS_DISSEMINATION_RESULTS = $results
$env:ORLEANS_DISSEMINATION_CANDIDATE_SHA = $selection.CandidateCommit
$env:ORLEANS_DISSEMINATION_MANUAL_RUN = 'true'
$env:ORLEANS_DISSEMINATION_SIZES = $Sizes
$env:ORLEANS_DISSEMINATION_ITERATIONS = $Iterations.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:ORLEANS_DISSEMINATION_REPETITIONS = $Repetitions.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:ORLEANS_DISSEMINATION_RUNTIME_PATHS = $RuntimePaths
$env:ORLEANS_DISSEMINATION_SCENARIOS = $Scenarios
$env:ORLEANS_DISSEMINATION_SILO_PROCESSOR_COUNT = $SiloProcessorCount.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:ORLEANS_DISSEMINATION_GC_CONSERVE_MEMORY = $GCConserveMemory.ToString([System.Globalization.CultureInfo]::InvariantCulture)
@{
    ToolingCommit = Get-Revision $root
    Selection = $selection
    Sizes = $Sizes
    Iterations = $Iterations
    Repetitions = $Repetitions
    RuntimePaths = $RuntimePaths
    Scenarios = $Scenarios
    SiloProcessorCount = $SiloProcessorCount
    GCConserveMemory = $GCConserveMemory
    StartUtc = [DateTimeOffset]::UtcNow
    ResultsDirectory = $results
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'invocation.json')

Push-Location $root
try {
    Invoke-DotNet @(
        $runner, '--filter-class', 'Orleans.Dissemination.PerformanceHarness.MeasurementTests',
        '--minimum-expected-tests', '21', '--report-trx',
        '--results-directory', (Join-Path $results 'instrument-checks')
    )
    Invoke-DotNet @(
        $runner, '--filter-class', 'Orleans.Dissemination.PerformanceHarness.ScalingTests',
        '--minimum-expected-tests', '1', '--report-trx', '--results-directory', $results
    )
}
finally {
    Pop-Location
}
