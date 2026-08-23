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

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
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
