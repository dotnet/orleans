[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string] $Framework,

    [Parameter(Mandatory)]
    [ValidateSet('BVT', 'SlowBVT', 'Functional')]
    [string] $Suite,

    [string] $CoverageToolPath,

    [switch] $DiscoverOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$solutionPath = Join-Path $resolvedRepositoryRoot 'Orleans.slnx'
$coverageSettings = Join-Path $resolvedRepositoryRoot '.github/coverage.config.xml'
$coverageDirectory = Join-Path $resolvedRepositoryRoot "TestResults/coverage-$Suite"
$filterQuery = "/[(Provider=None)&(Suite=$Suite)&(Area!=CodeGen)]"
$maximumXmlBytes = 100MB

function Read-XmlDocument {
    param([string] $Path)

    $file = Get-Item -LiteralPath $Path -Force
    $linkType = $file.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
    if ($file.Length -gt $maximumXmlBytes) {
        throw "$Path exceeds the 100 MB parsing limit"
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $maximumXmlBytes
    $reader = [Xml.XmlReader]::Create($file.FullName, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        try {
            $document.Load($reader)
        } catch [Xml.XmlException] {
            throw "$Path contains invalid XML: $($_.Exception.Message)"
        }
        return $document
    } finally {
        $reader.Dispose()
    }
}

$solution = Read-XmlDocument -Path $solutionPath
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

if ([string]::IsNullOrWhiteSpace($CoverageToolPath)) {
    throw 'CoverageToolPath is required when collecting coverage'
}
$resolvedCoverageToolPath = (Resolve-Path -LiteralPath $CoverageToolPath).Path

if (Test-Path -LiteralPath $coverageDirectory) {
    Remove-Item -LiteralPath $coverageDirectory -Recurse -Force
}
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
$index = 0
$totalTests = 0
foreach ($modulePath in $modules) {
    $index++
    $moduleName = [IO.Path]::GetFileNameWithoutExtension($modulePath)
    $coverageOutput = Join-Path $coverageDirectory ('{0}-{1:D3}-{2}.cobertura.xml' -f $Suite, $index, $moduleName)
    $reportDirectory = Join-Path ([IO.Path]::GetDirectoryName($modulePath)) 'TestResults'
    $reportPattern = "test_results_${Suite}_${Framework}_${moduleName}_${Framework}_*.trx"
    if (Test-Path -LiteralPath $reportDirectory) {
        Get-ChildItem -LiteralPath $reportDirectory -File -Filter $reportPattern |
            Remove-Item -Force
    }

    & $resolvedCoverageToolPath collect `
        --settings $coverageSettings `
        --output $coverageOutput `
        --output-format cobertura `
        --nologo `
        dotnet exec $modulePath `
        --filter-query $filterQuery `
        --hangdump --hangdump-timeout 10m `
        --crashdump --crashdump-type Full `
        --hangdump-type Full `
        --report-trx --report-trx-filename "test_results_${Suite}_${Framework}_{asm}_{tfm}_{arch}.trx" `
        --ignore-exit-code 8
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed for $modulePath with exit code $LASTEXITCODE"
    }

    $reports = @(Get-ChildItem -LiteralPath $reportDirectory -File -Filter $reportPattern)
    if ($reports.Count -ne 1) {
        throw "Expected one TRX report for $modulePath, found $($reports.Count)"
    }

    $report = $reports[0]
    $document = Read-XmlDocument -Path $report.FullName
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace('trx', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $counters = $document.SelectSingleNode('/trx:TestRun/trx:ResultSummary/trx:Counters', $namespaceManager)
    if (-not $counters) {
        throw "$($report.FullName) contains no test counters"
    }

    $moduleTestCount = [int] $counters.GetAttribute('total')
    $totalTests += $moduleTestCount
    if ($moduleTestCount -eq 0 -and (Test-Path -LiteralPath $coverageOutput)) {
        Remove-Item -LiteralPath $coverageOutput -Force
    } elseif ($moduleTestCount -gt 0) {
        $coverageDocument = Read-XmlDocument -Path $coverageOutput
        if (-not $coverageDocument.SelectSingleNode('//*[local-name()="line"]')) {
            throw "$coverageOutput contains no measured lines for $moduleTestCount tests"
        }
    }
}

if ($totalTests -eq 0) {
    throw "No tests ran for the $Suite suite on $Framework"
}

$coverageFiles = @(Get-ChildItem -LiteralPath $coverageDirectory -File -Filter '*.cobertura.xml')
if ($coverageFiles.Count -eq 0) {
    throw "The $Suite suite produced no measured coverage"
}

Write-Output "Ran $totalTests tests across $($modules.Count) modules and produced $($coverageFiles.Count) coverage files."
