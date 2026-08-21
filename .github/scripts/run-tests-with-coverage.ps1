[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string] $Framework,

    [Parameter(Mandatory)]
    [ValidateSet('BVT', 'SlowBVT', 'Functional')]
    [string] $Suite,

    [switch] $DiscoverOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$solutionPath = Join-Path $resolvedRepositoryRoot 'Orleans.slnx'
$coverageSettings = Join-Path $resolvedRepositoryRoot '.github/coverage.config.xml'
$coverageDirectory = Join-Path $resolvedRepositoryRoot "TestResults/coverage-$Suite"
$filterQuery = "/[(Provider=None)&(Suite=$Suite)&(Area!=CodeGen)]"

[xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
$projectPaths = @(
    $solution.SelectNodes('//Project[@Path]') |
        ForEach-Object { $_.GetAttribute('Path') } |
        Where-Object {
            $_.StartsWith('test/', [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetExtension($_) -in '.csproj', '.fsproj'
        } |
        Sort-Object
)

$modules = [Collections.Generic.List[string]]::new()
foreach ($relativeProjectPath in $projectPaths) {
    $projectPath = Join-Path $resolvedRepositoryRoot $relativeProjectPath
    $metadataJson = & dotnet msbuild $projectPath `
        -nologo `
        '-getProperty:TargetPath,IsTestingPlatformApplication' `
        "-property:TargetFramework=$Framework"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to evaluate test module metadata for $projectPath"
    }

    $metadata = $metadataJson | ConvertFrom-Json
    if ($metadata.Properties.IsTestingPlatformApplication -eq 'true' -and
        [IO.File]::Exists($metadata.Properties.TargetPath)) {
        $modules.Add($metadata.Properties.TargetPath)
    }
}

if ($modules.Count -eq 0) {
    throw "No $Framework MTP test modules were found"
}

if ($DiscoverOnly) {
    $modules
    return
}

if (Test-Path -LiteralPath $coverageDirectory) {
    Remove-Item -LiteralPath $coverageDirectory -Recurse -Force
}
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
$index = 0
foreach ($modulePath in $modules) {
    $index++
    $moduleName = [IO.Path]::GetFileNameWithoutExtension($modulePath)
    $coverageOutput = Join-Path $coverageDirectory ('{0:D3}-{1}.coverage' -f $index, $moduleName)
    & dotnet exec $modulePath `
        --filter-query $filterQuery `
        --hangdump --hangdump-timeout 10m `
        --crashdump --crashdump-type Full `
        --hangdump-type Full `
        --report-trx --report-trx-filename "test_results_${Suite}_${Framework}_{asm}_{tfm}_{arch}.trx" `
        --coverage `
        --coverage-output $coverageOutput `
        --coverage-output-format coverage `
        --coverage-settings $coverageSettings
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed for $modulePath with exit code $LASTEXITCODE"
    }
}

$reports = @(
    Get-ChildItem -LiteralPath $resolvedRepositoryRoot -Recurse -File -Filter "test_results_${Suite}_${Framework}_*.trx"
)
$totalTests = 0
foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace('trx', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $counters = $document.SelectSingleNode('/trx:TestRun/trx:ResultSummary/trx:Counters', $namespaceManager)
    if ($counters) {
        $totalTests += [int] $counters.GetAttribute('total')
    }
}

if ($totalTests -eq 0) {
    throw "No tests ran for the $Suite suite on $Framework"
}

$coverageFiles = @(Get-ChildItem -LiteralPath $coverageDirectory -File -Filter '*.coverage')
foreach ($coverageFile in $coverageFiles) {
    if ($coverageFile.Length -le 10) {
        Remove-Item -LiteralPath $coverageFile.FullName -Force
    }
}

$coverageFiles = @(Get-ChildItem -LiteralPath $coverageDirectory -File -Filter '*.coverage')
if ($coverageFiles.Count -eq 0) {
    throw "The $Suite suite produced no measured coverage"
}

Write-Output "Ran $totalTests tests across $($modules.Count) modules and produced $($coverageFiles.Count) coverage files."
