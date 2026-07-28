#requires -Version 7.0

[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $TargetFramework = "net10.0",
    [switch] $SkipBuild
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

function Test-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Root
    )

    $relativePath = [IO.Path]::GetRelativePath(
        [IO.Path]::GetFullPath($Root),
        [IO.Path]::GetFullPath($Path))
    return $relativePath -eq "." -or
        (-not [IO.Path]::IsPathRooted($relativePath) -and
        $relativePath -ne ".." -and
        -not $relativePath.StartsWith("..$([IO.Path]::DirectorySeparatorChar)"))
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$docfxRoot = Join-Path $repositoryRoot "docs\api"
$docfxConfig = Join-Path $docfxRoot "docfx.json"
$artifactRoot = Join-Path $docfxRoot ".artifacts"
$solutionPath = Join-Path $artifactRoot "api.slnx"
$metadataRoot = Join-Path $docfxRoot "reference"

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "Artifacts\docs\api"
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$outputMarkerName = ".orleans-api-docs-output"
$protectedRoots = @(
    (Join-Path $repositoryRoot ".config"),
    (Join-Path $repositoryRoot "docs\api"),
    (Join-Path $repositoryRoot "docs\scripts"),
    (Join-Path $repositoryRoot "src")
)

if ($OutputDirectory -eq $repositoryRoot -or
    $protectedRoots.Where({ Test-PathWithin -Path $OutputDirectory -Root $_ }).Count -gt 0) {
    throw "'$OutputDirectory' is within a source directory and cannot be used as the API documentation output."
}

Push-Location $repositoryRoot
try {
    Invoke-DotNet @("tool", "restore")

    if (-not $SkipBuild) {
        $binlogDirectory = Join-Path $repositoryRoot "Artifacts\docs"
        New-Item -ItemType Directory -Force $binlogDirectory | Out-Null
        Invoke-DotNet @(
            "build",
            "Orleans.slnx",
            "--configuration",
            $Configuration,
            "-bl:$(Join-Path $binlogDirectory 'api-build.binlog')"
        )
    }

    $projectFiles = Get-ChildItem (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj" |
        Sort-Object FullName
    $apiProjects = [Collections.Generic.List[object]]::new()

    foreach ($project in $projectFiles) {
        $evaluation = Get-ProjectProperties -ProjectPath $project.FullName -PropertyNames @(
            "IsPackable",
            "IncludeBuildOutput",
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
            "TargetPath"
        )
        $targetPath = $targetEvaluation.Properties.TargetPath
        if (-not $targetPath) {
            throw "MSBuild did not return a target path for '$($project.FullName)' and '$TargetFramework'."
        }

        $apiProjects.Add([pscustomobject]@{
            ProjectPath = $project.FullName
            AssemblyName = $targetEvaluation.Properties.AssemblyName
            TargetPath = [IO.Path]::GetFullPath($targetPath)
        })
    }

    if ($apiProjects.Count -eq 0) {
        throw "No packable source projects with build output target '$TargetFramework'."
    }

    $duplicateAssemblies = $apiProjects |
        Group-Object AssemblyName |
        Where-Object Count -gt 1
    if ($duplicateAssemblies) {
        throw "Multiple API projects produce the same assembly: $($duplicateAssemblies.Name -join ', ')."
    }

    Write-Host "Selected $($apiProjects.Count) API projects:"
    foreach ($project in $apiProjects) {
        Write-Host "  $($project.AssemblyName) <- $([IO.Path]::GetRelativePath($repositoryRoot, $project.ProjectPath))"
    }

    $missingProjects = @($apiProjects | Where-Object { -not (Test-Path $_.TargetPath) })
    if ($missingProjects.Count -gt 0 -and $SkipBuild) {
        throw "Missing '$($missingProjects[0].TargetPath)'. Build the repository or omit -SkipBuild."
    }

    foreach ($project in $missingProjects) {
        Write-Host "Building API project not included in Orleans.slnx: $($project.AssemblyName)"
        Invoke-DotNet @(
            "build",
            $project.ProjectPath,
            "--configuration",
            $Configuration,
            "--framework",
            $TargetFramework
        )
    }

    Remove-Item $artifactRoot, $metadataRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $artifactRoot | Out-Null

    foreach ($project in $apiProjects) {
        if (-not (Test-Path $project.TargetPath)) {
            throw "Build completed without producing '$($project.TargetPath)'."
        }

        $xmlDocumentation = [IO.Path]::ChangeExtension($project.TargetPath, ".xml")
        if (-not (Test-Path $xmlDocumentation)) {
            throw "Missing XML documentation '$xmlDocumentation'."
        }
    }

    $solutionContents = [Collections.Generic.List[string]]::new()
    $solutionContents.Add("<Solution>")
    foreach ($project in $apiProjects) {
        $relativeProjectPath = [IO.Path]::GetRelativePath($artifactRoot, $project.ProjectPath).Replace("\", "/")
        $solutionContents.Add("  <Project Path=`"$relativeProjectPath`" />")
    }
    $solutionContents.Add("</Solution>")
    Set-Content $solutionPath $solutionContents -Encoding utf8

    $docfxProperties = "Configuration=$Configuration;TargetFramework=$TargetFramework;TreatWarningsAsErrors=false"
    Invoke-DotNet @(
        "docfx",
        "metadata",
        $docfxConfig,
        "--property",
        $docfxProperties,
        "--logLevel",
        "warning"
    )

    $metadataFiles = @(Get-ChildItem $metadataRoot -Filter "*.yml")
    if ($metadataFiles.Count -eq 0) {
        throw "DocFX did not generate managed-reference metadata."
    }

    $uids = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($metadataFile in $metadataFiles) {
        if ((Get-Content $metadataFile.FullName -First 1) -ne "### YamlMime:ManagedReference") {
            continue
        }

        $insideItems = $false
        foreach ($line in Get-Content $metadataFile.FullName) {
            if ($line -eq "items:") {
                $insideItems = $true
                continue
            }

            if ($line -eq "references:") {
                $insideItems = $false
                continue
            }

            if ($insideItems -and $line -match "^- uid:\s*(.+)$") {
                $uid = $Matches[1]
                if ($uids.ContainsKey($uid)) {
                    throw "Duplicate API UID '$uid' appears in '$($uids[$uid])' and '$($metadataFile.Name)'."
                }

                $uids.Add($uid, $metadataFile.Name)
            }
        }
    }

    if (Test-Path $OutputDirectory) {
        $outputMarker = Join-Path $OutputDirectory $outputMarkerName
        $existingItems = @(Get-ChildItem $OutputDirectory -Force)
        if ($existingItems.Count -gt 0 -and -not (Test-Path $outputMarker -PathType Leaf)) {
            throw "'$OutputDirectory' is not empty and was not created by this script."
        }

        Remove-Item $OutputDirectory -Recurse -Force
    }

    Invoke-DotNet @("docfx", "build", $docfxConfig, "--output", $OutputDirectory, "--logLevel", "warning")
    Set-Content (Join-Path $OutputDirectory $outputMarkerName) "Generated by docs/scripts/Build-ApiDocs.ps1" -Encoding ascii

    $indexPath = Join-Path $OutputDirectory "index.html"
    $apiPages = @(Get-ChildItem (Join-Path $OutputDirectory "reference") -Recurse -Filter "*.html")
    if (-not (Test-Path $indexPath) -or $apiPages.Count -eq 0) {
        throw "DocFX did not produce the expected landing page and API reference pages."
    }

    $rootRelativeLinks = Get-ChildItem $OutputDirectory -Recurse -Filter "*.html" |
        Select-String -Pattern '(?:href|src)="/(?!/)' |
        Select-Object -First 1
    if ($rootRelativeLinks) {
        throw "Generated page '$($rootRelativeLinks.Path)' contains a root-relative link and cannot be mounted safely below /orleans/api/."
    }

    Write-Host ""
    Write-Host "Generated $($apiProjects.Count) assemblies, $($uids.Count) API UIDs, and $($apiPages.Count) API pages."
    Write-Host "Output: $OutputDirectory"
}
finally {
    Pop-Location
}
