[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string] $ResultsDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('BVT', 'SlowBVT', 'Functional')]
    [string] $Suite,

    [switch] $DiscoverOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maximumReportBytes = 100MB
$trxNamespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$resolvedTestRoot = [IO.Path]::GetFullPath((Join-Path $resolvedRepositoryRoot 'test'))
$coverageDirectory = Join-Path $resolvedRepositoryRoot "TestResults/coverage-$Suite"
[void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)

function Read-Trx {
    param([IO.FileInfo] $Report)

    $linkType = $Report.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($Report.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$($Report.FullName) must not be a symbolic link"
    }
    if ($Report.Length -gt $maximumReportBytes) {
        throw "$($Report.FullName) exceeds the 100 MB parsing limit"
    }

    $encoding = [Text.UTF8Encoding]::new($false, $true)
    try {
        $reportText = $encoding.GetString([IO.File]::ReadAllBytes($Report.FullName))
    } catch [Text.DecoderFallbackException] {
        throw "$($Report.FullName) must contain valid UTF-8"
    }
    if ($reportText.Length -gt 0 -and $reportText[0] -eq [char] 0xfeff) {
        $reportText = $reportText.Substring(1)
    }
    if ($reportText.IndexOf('<!DOCTYPE', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $reportText.IndexOf('<!ENTITY', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$($Report.FullName) contains unsupported XML declarations"
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $maximumReportBytes
    $stringReader = [IO.StringReader]::new($reportText)
    $reader = [Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        try {
            $document.Load($reader)
        } catch [Xml.XmlException] {
            throw "$($Report.FullName) contains invalid XML: $($_.Exception.Message)"
        }
        return $document
    } finally {
        $reader.Dispose()
        $stringReader.Dispose()
    }
}

$modules = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -File -Filter '*.trx')
if ($reports.Count -eq 0) {
    throw "No TRX reports found under $ResultsDirectory"
}

foreach ($report in $reports) {
    $document = Read-Trx $report
    $documentNamespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $documentNamespaceManager.AddNamespace('trx', $trxNamespace)
    $counters = $document.SelectSingleNode('/trx:TestRun/trx:ResultSummary/trx:Counters', $documentNamespaceManager)
    if (-not $counters -or [int] $counters.GetAttribute('total') -eq 0) {
        continue
    }

    $testMethod = $document.SelectSingleNode(
        '/trx:TestRun/trx:TestDefinitions/trx:UnitTest/trx:TestMethod',
        $documentNamespaceManager
    )
    if (-not $testMethod -or -not $testMethod.GetAttribute('codeBase')) {
        throw "$($report.FullName) does not identify its test module"
    }

    $codeBase = $testMethod.GetAttribute('codeBase').Replace('\', '/')
    $testMarker = '/test/'
    $markerIndex = $codeBase.IndexOf($testMarker, [StringComparison]::OrdinalIgnoreCase)
    if ($markerIndex -lt 0) {
        throw "$($report.FullName) references a module outside test/"
    }

    $relativeModule = $codeBase.Substring($markerIndex + 1).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $modulePath = [IO.Path]::GetFullPath((Join-Path $resolvedRepositoryRoot $relativeModule))
    if (-not $modulePath.StartsWith("$resolvedTestRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($report.FullName) references a module outside test/"
    }
    if ([IO.Path]::GetExtension($modulePath) -ne '.dll' -or -not [IO.File]::Exists($modulePath)) {
        throw "Test module does not exist: $modulePath"
    }
    $module = Get-Item -LiteralPath $modulePath -Force
    $moduleLinkType = $module.PSObject.Properties['LinkType']
    if (($moduleLinkType -and $moduleLinkType.Value) -or ($module.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$modulePath must not be a symbolic link"
    }
    if (-not [IO.File]::Exists([IO.Path]::ChangeExtension($modulePath, '.testconfig.json'))) {
        throw "Test module has no configuration: $modulePath"
    }

    [void] $modules.Add($modulePath)
}

if ($modules.Count -eq 0) {
    throw "TRX reports contain no selected $Suite test modules"
}

$selectedModules = @($modules | Sort-Object)
if ($DiscoverOnly) {
    $selectedModules
    return
}

$filterQuery = "/[(Provider=None)&(Suite=$Suite)&(Area!=CodeGen)]"
$coverageSettings = Join-Path $resolvedRepositoryRoot '.github/coverage.config.xml'
$index = 0
foreach ($modulePath in $selectedModules) {
    $index++
    $moduleName = [IO.Path]::GetFileNameWithoutExtension($modulePath)
    $coverageOutput = Join-Path $coverageDirectory ('{0:D3}-{1}.coverage' -f $index, $moduleName)
    & dotnet exec $modulePath `
        --filter-query $filterQuery `
        --minimum-expected-tests 1 `
        --hangdump --hangdump-timeout 10m `
        --crashdump --crashdump-type Full `
        --hangdump-type Full `
        --coverage `
        --coverage-output $coverageOutput `
        --coverage-output-format coverage `
        --coverage-settings $coverageSettings
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage failed for $modulePath with exit code $LASTEXITCODE"
    }
}
