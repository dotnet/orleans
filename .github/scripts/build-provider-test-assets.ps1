[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Framework,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [Parameter(Mandatory)]
    [string] $Projects
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$relativeOutputDirectory = [IO.Path]::GetRelativePath($repositoryRoot, $resolvedOutputDirectory)
if ($relativeOutputDirectory -eq '..' -or $relativeOutputDirectory.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")) {
    throw "$resolvedOutputDirectory must be within $repositoryRoot"
}

$currentPath = $repositoryRoot
foreach ($segment in $relativeOutputDirectory.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
    $currentPath = Join-Path $currentPath $segment
    Assert-NotReparsePoint $currentPath
}

Remove-Item -LiteralPath $resolvedOutputDirectory -Recurse -Force -ErrorAction SilentlyContinue

foreach ($project in $Projects.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
    dotnet build $project --framework $Framework -p:ContinuousIntegrationBuild=false
    if ($LASTEXITCODE -ne 0) {
        throw "Provider test build failed for $project with exit code $LASTEXITCODE"
    }

    $projectDirectory = [IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $project).Path)
    $sourceDirectory = Join-Path $projectDirectory "bin/Debug/$Framework"
    $relativeDirectory = [IO.Path]::GetRelativePath($repositoryRoot, $sourceDirectory)
    $destinationDirectory = Join-Path $resolvedOutputDirectory $relativeDirectory
    [void] (New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destinationDirectory)))
    Copy-Item -LiteralPath $sourceDirectory -Destination $destinationDirectory -Recurse
}
