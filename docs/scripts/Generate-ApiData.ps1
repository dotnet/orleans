# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#
# Adapted from microsoft/aspire.dev's generate-package-json.ps1 at
# 9aa68083af47da79b63bc15b80f44c1927bf9c08.

#requires -Version 7.0

[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $TargetFramework = "net10.0",
    [int] $Parallelism = 0,
    [switch] $SkipBuild,
    [string] $SourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-ProjectProperties {
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath,
        [Parameter(Mandatory)]
        [string[]] $PropertyNames,
        [string] $Framework
    )

    $arguments = @(
        "msbuild",
        $ProjectPath,
        "-nologo",
        "-getProperty:$($PropertyNames -join ',')",
        "-p:Configuration=$Configuration"
    )

    if ($Framework) {
        $arguments += "-p:TargetFramework=$Framework"
    }

    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to evaluate '$ProjectPath':`n$($output -join [Environment]::NewLine)"
    }

    try {
        return ($output -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        throw "MSBuild returned invalid property data for '$ProjectPath':`n$($output -join [Environment]::NewLine)"
    }
}

function Get-DotNetRoot {
    $sdkLines = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to locate the .NET SDK installation."
    }

    foreach ($line in @($sdkLines) | Select-Object -Last 1) {
        if ($line -match "\[(.+)[/\\]sdk\]\s*$") {
            return [IO.Path]::GetFullPath($Matches[1])
        }
    }

    throw "Unable to determine the .NET installation root from 'dotnet --list-sdks'."
}

function Get-FrameworkReferences {
    param(
        [Parameter(Mandatory)]
        [string] $DotNetRoot,
        [Parameter(Mandatory)]
        [string] $Framework
    )

    $references = [Collections.Generic.List[string]]::new()
    foreach ($packName in @("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref")) {
        $packRoot = Join-Path $DotNetRoot "packs\$packName"
        if (-not (Test-Path $packRoot)) {
            continue
        }

        $referenceDirectory = Get-ChildItem $packRoot -Directory |
            Sort-Object { [version]($_.Name.Split("-", 2)[0]) } -Descending |
            ForEach-Object { Join-Path $_.FullName "ref\$Framework" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1

        if ($referenceDirectory) {
            foreach ($assembly in Get-ChildItem $referenceDirectory -Filter "*.dll" | Sort-Object Name) {
                $references.Add($assembly.FullName)
            }
        }
    }

    if ($references.Count -eq 0) {
        throw "No framework reference assemblies were found for '$Framework'."
    }

    return $references
}

function Get-PackageCompileReferences {
    param(
        [Parameter(Mandatory)]
        [string] $AssetsFile,
        [Parameter(Mandatory)]
        [string] $Framework
    )

    if (-not (Test-Path $AssetsFile)) {
        throw "Missing NuGet assets file '$AssetsFile'."
    }

    $assets = Get-Content $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
    if (-not $assets["targets"].ContainsKey($Framework)) {
        throw "NuGet assets '$AssetsFile' do not contain '$Framework'."
    }

    $references = [Collections.Generic.List[string]]::new()
    foreach ($library in $assets["targets"][$Framework].GetEnumerator()) {
        if (-not $library.Value.ContainsKey("compile") -or
            -not $assets["libraries"].ContainsKey($library.Key) -or
            $assets["libraries"][$library.Key]["type"] -ne "package") {
            continue
        }

        $packagePath = $assets["libraries"][$library.Key]["path"]
        foreach ($compileAsset in $library.Value["compile"].Keys | Sort-Object) {
            if (-not $compileAsset.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $resolvedPath = $null
            foreach ($packageFolder in $assets["packageFolders"].Keys) {
                $candidate = Join-Path $packageFolder (Join-Path $packagePath $compileAsset)
                if (Test-Path $candidate) {
                    $resolvedPath = [IO.Path]::GetFullPath($candidate)
                    break
                }
            }

            if (-not $resolvedPath) {
                throw "Unable to locate NuGet compile asset '$compileAsset' for '$($library.Key)'."
            }

            $references.Add($resolvedPath)
        }
    }

    return $references
}

function Add-Reference {
    param(
        [Parameter(Mandatory)]
        [Collections.Generic.Dictionary[string, string]] $References,
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Kind
    )

    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($Path).Name
    if (-not $assemblyName) {
        throw "Unable to read the assembly identity from '$Path'."
    }

    $existingPath = $null
    if ($References.TryGetValue($assemblyName, [ref] $existingPath)) {
        if ($existingPath -eq $Path) {
            return
        }

        if ($Kind -eq "framework") {
            return
        }

        $existingHash = (Get-FileHash -Algorithm SHA256 $existingPath).Hash
        $candidateHash = (Get-FileHash -Algorithm SHA256 $Path).Hash
        if ($existingHash -ne $candidateHash) {
            throw "Conflicting '$assemblyName' references were found at '$existingPath' and '$Path'."
        }

        return
    }

    $References.Add($assemblyName, $Path)
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$toolProject = Join-Path $repositoryRoot "docs\tools\PackageJsonGenerator\PackageJsonGenerator.csproj"
$artifactRoot = Join-Path $repositoryRoot "Artifacts\docs\package-json-generator"
$manifestPath = Join-Path $artifactRoot "manifest.json"
$sourceFileManifestPath = Join-Path $artifactRoot "source-files.txt"

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "docs\site\src\data\pkgs"
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$sourceStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=all -- src)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the src working tree."
}

if ($sourceStatus.Count -gt 0) {
    throw "The src tree has uncommitted or untracked changes. Commit them before generating source-linked API data."
}

if (-not $SourceCommit) {
    $SourceCommit = (& git -C $repositoryRoot log -1 --format=%H -- src).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine the latest source commit."
    }
}

if ($SourceCommit -notmatch "^[0-9a-fA-F]{40}$") {
    throw "'$SourceCommit' is not a full Git commit SHA."
}

& git -C $repositoryRoot diff --quiet $SourceCommit -- src
if ($LASTEXITCODE -ne 0) {
    throw "The local src tree does not match source commit '$SourceCommit'."
}

if ($Parallelism -le 0) {
    $Parallelism = [Math]::Max(1, [Environment]::ProcessorCount)
}

Push-Location $repositoryRoot
try {
    $projectFiles = Get-ChildItem (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj" |
        Sort-Object FullName
    $apiProjects = [Collections.Generic.List[object]]::new()

    foreach ($project in $projectFiles) {
        $evaluation = Get-ProjectProperties -ProjectPath $project.FullName -PropertyNames @(
            "IncludeBuildOutput",
            "IsPackable",
            "TargetFramework",
            "TargetFrameworks"
        )

        if ($evaluation.Properties.IsPackable -ne "true" -or
            $evaluation.Properties.IncludeBuildOutput -ne "true") {
            continue
        }

        $frameworks = if ($evaluation.Properties.TargetFrameworks) {
            @($evaluation.Properties.TargetFrameworks -split ";")
        }
        else {
            @($evaluation.Properties.TargetFramework)
        }

        if ($TargetFramework -notin $frameworks) {
            continue
        }

        $targetEvaluation = Get-ProjectProperties -ProjectPath $project.FullName -Framework $TargetFramework -PropertyNames @(
            "AssemblyName",
            "PackageId",
            "PackageVersion",
            "ProjectAssetsFile",
            "TargetPath"
        )

        $apiProjects.Add([pscustomobject]@{
            AssemblyName = $targetEvaluation.Properties.AssemblyName
            AssetsFile = [IO.Path]::GetFullPath($targetEvaluation.Properties.ProjectAssetsFile)
            PackageId = $targetEvaluation.Properties.PackageId
            PackageVersion = $targetEvaluation.Properties.PackageVersion
            ProjectPath = $project.FullName
            TargetPath = [IO.Path]::GetFullPath($targetEvaluation.Properties.TargetPath)
        })
    }

    if ($apiProjects.Count -eq 0) {
        throw "No packable source projects with build output target '$TargetFramework'."
    }

    $duplicatePackages = $apiProjects | Group-Object PackageId | Where-Object Count -gt 1
    if ($duplicatePackages) {
        throw "Multiple projects produce the same package: $($duplicatePackages.Name -join ', ')."
    }

    $duplicateAssemblies = $apiProjects | Group-Object AssemblyName | Where-Object Count -gt 1
    if ($duplicateAssemblies) {
        throw "Multiple projects produce the same assembly: $($duplicateAssemblies.Name -join ', ')."
    }

    Write-Host "Selected $($apiProjects.Count) package projects for $TargetFramework."
    if (-not $SkipBuild) {
        foreach ($project in $apiProjects) {
            Write-Host "Building $($project.PackageId)"
            Invoke-DotNet @(
                "build",
                $project.ProjectPath,
                "--configuration",
                $Configuration,
                "--framework",
                $TargetFramework,
                "--verbosity",
                "quiet"
            )
        }
    }

    Invoke-DotNet @(
        "build",
        $toolProject,
        "--configuration",
        "Release",
        "--verbosity",
        "quiet"
    )

    foreach ($project in $apiProjects) {
        if (-not (Test-Path $project.TargetPath)) {
            throw "Missing '$($project.TargetPath)'. Build the projects or omit -SkipBuild."
        }

        $xmlDocumentation = [IO.Path]::ChangeExtension($project.TargetPath, ".xml")
        if (-not (Test-Path $xmlDocumentation)) {
            throw "Missing XML documentation '$xmlDocumentation'."
        }
    }

    $dotNetRoot = Get-DotNetRoot
    $frameworkReferences = Get-FrameworkReferences -DotNetRoot $dotNetRoot -Framework $TargetFramework
    $allProjectAssemblies = @($apiProjects.TargetPath | Sort-Object)

    New-Item -ItemType Directory -Force $artifactRoot, $OutputDirectory | Out-Null
    $trackedSourceFiles = @(& git -C $repositoryRoot ls-tree -r --name-only $SourceCommit -- src)
    if ($LASTEXITCODE -ne 0 -or $trackedSourceFiles.Count -eq 0) {
        throw "Unable to enumerate source files at '$SourceCommit'."
    }
    $trackedSourceFiles | Sort-Object -Unique | Set-Content $sourceFileManifestPath -Encoding utf8

    $manifestPackages = [Collections.Generic.List[object]]::new()
    $expectedOutputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($project in $apiProjects | Sort-Object PackageId) {
        $references = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)

        foreach ($projectAssembly in $allProjectAssemblies) {
            if ($projectAssembly -ne $project.TargetPath) {
                Add-Reference -References $references -Path $projectAssembly -Kind "project"
            }
        }

        foreach ($packageAssembly in Get-PackageCompileReferences -AssetsFile $project.AssetsFile -Framework $TargetFramework) {
            Add-Reference -References $references -Path $packageAssembly -Kind "package"
        }

        foreach ($frameworkAssembly in $frameworkReferences) {
            Add-Reference -References $references -Path $frameworkAssembly -Kind "framework"
        }

        $outputFileName = "$($project.PackageId).$($project.PackageVersion).json"
        if ($outputFileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "Package '$($project.PackageId)' produced an invalid output file name '$outputFileName'."
        }

        $outputPath = Join-Path $OutputDirectory $outputFileName
        $expectedOutputs.Add($outputPath) | Out-Null
        $manifestPackages.Add([ordered]@{
            input = $project.TargetPath
            references = @($references.Values | Sort-Object)
            output = $outputPath
            packageVersion = $project.PackageVersion
            packageName = $project.PackageId
            sourceRepo = "https://github.com/dotnet/orleans"
            sourceCommit = $SourceCommit.ToLowerInvariant()
            sourceRoot = $repositoryRoot
            sourceFileManifest = $sourceFileManifestPath
            targetFramework = $TargetFramework
        })
    }

    $manifest = [ordered]@{ packages = $manifestPackages }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding utf8

    Invoke-DotNet @(
        "run",
        "--project",
        $toolProject,
        "--configuration",
        "Release",
        "--no-build",
        "--",
        "batch",
        "--manifest",
        $manifestPath,
        "--parallelism",
        $Parallelism
    )

    foreach ($existingFile in Get-ChildItem $OutputDirectory -Filter "*.json") {
        if ($expectedOutputs.Contains($existingFile.FullName)) {
            continue
        }

        try {
            $existingDocument = Get-Content $existingFile.FullName -Raw | ConvertFrom-Json
        }
        catch {
            continue
        }

        $packageProperty = $existingDocument.PSObject.Properties["package"]
        if (-not $packageProperty) {
            continue
        }

        $package = $packageProperty.Value
        $repositoryProperty = $package.PSObject.Properties["sourceRepository"]
        $nameProperty = $package.PSObject.Properties["name"]
        if ($repositoryProperty -and
            $nameProperty -and
            $repositoryProperty.Value -eq "https://github.com/dotnet/orleans" -and
            $nameProperty.Value -like "Microsoft.Orleans*") {
            Remove-Item $existingFile.FullName
        }
    }

    $typeCount = 0
    $memberCount = 0
    foreach ($project in $apiProjects) {
        $outputPath = Join-Path $OutputDirectory "$($project.PackageId).$($project.PackageVersion).json"
        if (-not (Test-Path $outputPath)) {
            throw "The generator did not produce '$outputPath'."
        }

        $document = Get-Content $outputPath -Raw | ConvertFrom-Json
        if ($document.package.name -ne $project.PackageId -or
            $document.package.version -ne $project.PackageVersion -or
            $document.package.targetFramework -ne $TargetFramework -or
            $document.package.sourceRepository -ne "https://github.com/dotnet/orleans" -or
            $document.package.sourceCommit -ne $SourceCommit.ToLowerInvariant()) {
            throw "Package metadata validation failed for '$outputPath'."
        }

        $duplicateTypes = @($document.types | Group-Object fullName | Where-Object Count -gt 1)
        if ($duplicateTypes.Count -gt 0) {
            throw "Duplicate API types were generated for '$($project.PackageId)': $($duplicateTypes.Name -join ', ')."
        }

        $typeCount += @($document.types).Count
        $memberCount += @($document.types | ForEach-Object { @($_.members).Count } | Measure-Object -Sum).Sum
    }

    Write-Host ""
    Write-Host "Generated $($apiProjects.Count) package files with $typeCount public types and $memberCount declared members."
    Write-Host "Output: $OutputDirectory"
}
finally {
    Pop-Location
}
