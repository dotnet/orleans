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

# Samples are copied out of the repository and built standalone, so each one carries its own
# Directory.Packages.props and must not reference anything outside its own directory.
$rootPackageProps = [System.IO.Path]::GetFullPath((Join-Path $samplesRoot 'Directory.Packages.props'))
$packageVersions = @{}
foreach ($projectPath in $projectFiles) {
    $projectFullPath = [System.IO.Path]::GetFullPath((Join-Path $samplesRoot $projectPath))
    $unitRoot = $null
    for ($directory = (Split-Path -Parent $projectFullPath); $directory -and $directory.StartsWith($normalizedSamplesRoot, $pathComparison); $directory = (Split-Path -Parent $directory)) {
        $candidate = Join-Path $directory 'Directory.Packages.props'
        if (Test-Path -LiteralPath $candidate) {
            $unitRoot = $directory
            break
        }
    }

    if (-not $unitRoot -or [System.IO.Path]::GetFullPath((Join-Path $unitRoot 'Directory.Packages.props')) -eq $rootPackageProps) {
        throw "$projectPath is not covered by a sample-level Directory.Packages.props."
    }

    [xml] $packageProps = Get-Content -LiteralPath (Join-Path $unitRoot 'Directory.Packages.props')
    $declaredVersions = @{}
    foreach ($packageVersion in $packageProps.SelectNodes('//PackageVersion')) {
        $declaredVersions[$packageVersion.Include] = $packageVersion.Version
        $existing = $packageVersions[$packageVersion.Include]
        if ($existing -and $existing -ne $packageVersion.Version) {
            throw "Package '$($packageVersion.Include)' is pinned to both '$existing' and '$($packageVersion.Version)'."
        }

        $packageVersions[$packageVersion.Include] = $packageVersion.Version
    }

    [xml] $project = Get-Content -LiteralPath $projectFullPath
    foreach ($reference in $project.SelectNodes('//PackageReference')) {
        if (-not $declaredVersions.ContainsKey($reference.Include)) {
            throw "$projectPath references '$($reference.Include)', which has no version in $([System.IO.Path]::GetRelativePath($samplesRoot, $unitRoot))/Directory.Packages.props."
        }
    }

    $normalizedUnitRoot = $unitRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($reference in $project.SelectNodes('//ProjectReference')) {
        $referencePath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $projectFullPath) $reference.Include.Replace('\', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not $referencePath.StartsWith($normalizedUnitRoot, $pathComparison)) {
            throw "$projectPath references '$($reference.Include)' outside its own sample; samples must build standalone."
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
