[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prepare', 'Validate')]
    [string] $Action,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string] $CoverageId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumXmlBytes = 100MB

function Assert-NotReparsePoint {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$coverageDirectory = Join-Path $repositoryRoot 'TestResults'
$coverageOutput = Join-Path $coverageDirectory "$CoverageId.cobertura.xml"

if ($Action -eq 'Prepare') {
    Assert-NotReparsePoint $coverageDirectory
    [void] (New-Item -ItemType Directory -Force -Path $coverageDirectory)
    Assert-NotReparsePoint $coverageOutput
    Remove-Item -LiteralPath $coverageOutput -Force -ErrorAction SilentlyContinue
    return
}

Assert-NotReparsePoint $coverageDirectory
Assert-NotReparsePoint $coverageOutput
$output = Get-Item -LiteralPath $coverageOutput -Force
if ($output.Length -gt $maximumXmlBytes) {
    throw "$coverageOutput exceeds the 100 MB parsing limit"
}

$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = $maximumXmlBytes
$reader = [Xml.XmlReader]::Create($output.FullName, $settings)
try {
    $coverage = [Xml.XmlDocument]::new()
    $coverage.XmlResolver = $null
    $coverage.Load($reader)
    if (-not $coverage.SelectSingleNode('//*[local-name()="line"]')) {
        throw "$coverageOutput contains no measured lines"
    }
} finally {
    $reader.Dispose()
}
