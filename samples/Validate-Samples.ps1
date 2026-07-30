[CmdletBinding()]
param(
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$samplesRoot = $PSScriptRoot
$manifestPath = Join-Path $samplesRoot 'gallery.json'
$solutionPath = Join-Path $samplesRoot 'Samples.slnx'
$requiredProperties = @('slug', 'title', 'description', 'path', 'sourceRepository', 'image', 'languages', 'tags', 'featured')
$entries = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$normalizedSamplesRoot = [System.IO.Path]::GetFullPath($samplesRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$pathComparison = if ($IsWindows) {
    [System.StringComparison]::OrdinalIgnoreCase
}
else {
    [System.StringComparison]::Ordinal
}

if ($entries.Count -eq 0) {
    throw 'gallery.json contains no samples.'
}

$slugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($entry in $entries) {
    $actualProperties = @($entry.PSObject.Properties.Name)
    if (($actualProperties -join ',') -ne ($requiredProperties -join ',')) {
        throw "Gallery entry '$($entry.slug)' does not match the required schema."
    }

    if (-not $slugs.Add($entry.slug)) {
        throw "Duplicate gallery slug '$($entry.slug)'."
    }

    foreach ($relativePath in @($entry.path, $entry.image)) {
        if ($null -eq $relativePath) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $samplesRoot $relativePath))
        if (-not $fullPath.StartsWith($normalizedSamplesRoot, $pathComparison)) {
            throw "Gallery entry '$($entry.slug)' contains a path outside samples: '$relativePath'."
        }

        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Gallery entry '$($entry.slug)' references missing path '$relativePath'."
        }
    }
}

& (Join-Path $samplesRoot 'Update-Readme.ps1') -Check

$forbiddenDirectories = Get-ChildItem -LiteralPath $samplesRoot -Recurse -Force -Directory |
    Where-Object Name -In @('.git', '.vs', 'node_modules', 'artifacts', 'Artifacts')
if ($forbiddenDirectories) {
    throw "Generated or repository metadata directories are present: $($forbiddenDirectories.FullName -join ', ')"
}

$projectFiles = Get-ChildItem -LiteralPath $samplesRoot -Recurse -File -Include '*.csproj', '*.fsproj', '*.vbproj' |
    ForEach-Object { [System.IO.Path]::GetRelativePath($samplesRoot, $_.FullName).Replace('\', '/') } |
    Sort-Object

[xml] $solution = Get-Content -LiteralPath $solutionPath
$solutionProjects = @(
    $solution.SelectNodes('//Project') |
        Where-Object { -not $_.Path.StartsWith('../') } |
        ForEach-Object { $_.Path.Replace('\', '/') }
) | Sort-Object
if (($projectFiles -join "`n") -ne ($solutionProjects -join "`n")) {
    throw 'Samples.slnx does not contain exactly the projects under samples.'
}

foreach ($projectPath in $projectFiles) {
    [xml] $project = Get-Content -LiteralPath (Join-Path $samplesRoot $projectPath)
    foreach ($reference in $project.SelectNodes('//PackageReference')) {
        if ($reference.Version -or $reference.VersionOverride) {
            throw "$projectPath contains a non-central package version for '$($reference.Include)'."
        }
    }
}

Write-Host "Validated $($entries.Count) gallery entries and $($projectFiles.Count) projects."

if (-not $NoBuild) {
    & dotnet build $solutionPath --configuration Release --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Sample build failed with exit code $LASTEXITCODE."
    }
}
