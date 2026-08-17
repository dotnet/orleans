<#
.SYNOPSIS
    Validates all Orleans documentation snippet projects.

.DESCRIPTION
    This script finds all .csproj files in documentation snippet directories, verifies
    that they target net10.0 and reference Orleans 10.2.2 packages, then runs
    'dotnet build' on ordinary projects or 'dotnet test' on projects which declare
    IsTestProject=true.
    
    Use this script to validate snippet code after making changes to ensure all
    documentation examples remain buildable and executable test examples pass.

.PARAMETER Parallel
    Run validations in dependency-safe parallel batches (default: false for clearer output)

.PARAMETER PolicyOnly
    Validate target frameworks and Orleans package versions without building.

.PARAMETER RootPath
    Root directory to scan. Defaults to the documentation content directory.

.PARAMETER SiteRootPath
    Documentation site boundary for resolving includes. Defaults to docs/site for the
    repository documentation, or RootPath when validating a fixture.

.EXAMPLE
    .\validate-snippets.ps1
    
    Validates all snippet projects sequentially and reports results.

.EXAMPLE
    .\validate-snippets.ps1 -Parallel
    
    Validates independent snippet projects in parallel while serializing projects whose
    transitive project-reference closures overlap.

.NOTES
    Exit codes:
    0 - All projects validated successfully
    1 - One or more projects failed validation
#>

param(
    [switch]$Parallel = $false,
    [switch]$PolicyOnly = $false,
    [string]$RootPath = $PSScriptRoot,
    [string]$SiteRootPath,
    [string]$ProjectPolicyPath
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$validationRoot = (Resolve-Path -LiteralPath $RootPath).Path
$resolvedProjectPolicyPath = $(if ($ProjectPolicyPath) {
    (Resolve-Path -LiteralPath $ProjectPolicyPath).Path
} else {
    [IO.Path]::GetFullPath([IO.Path]::Combine($scriptDir, "../../../../project-policy.json"))
})
$projectPolicy = Get-Content -LiteralPath $resolvedProjectPolicyPath -Raw | ConvertFrom-Json
$requiredOrleansVersion = [string]$projectPolicy.orleansPackageVersion

Write-Host "Orleans Documentation Snippet Validator" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find all .csproj files in snippets directories
$snippetProjects = @(Get-ChildItem -Path $validationRoot -Recurse -Filter "*.csproj" |
    Where-Object { $_.FullName -match "snippets" } |
    Select-Object -ExpandProperty FullName)

function Test-IsTestProject {
    param([string]$ProjectPath)

    [xml] $projectXml = Get-Content -LiteralPath $ProjectPath -Raw
    return $projectXml.Project.PropertyGroup.IsTestProject -contains "true"
}

if ($snippetProjects.Count -eq 0) {
    Write-Host "No snippet projects found!" -ForegroundColor Yellow
}

Write-Host "Found $($snippetProjects.Count) snippet project(s) to validate:" -ForegroundColor Green
$snippetProjects | ForEach-Object { 
    $relativePath = [IO.Path]::GetRelativePath($validationRoot, $_)
    $action = $(if (Test-IsTestProject -ProjectPath $_) { "test" } else { "build" })
    Write-Host "  - $relativePath ($action)" -ForegroundColor Gray
}
Write-Host ""

function Get-LineNumber {
    param(
        [string]$Path,
        [string]$Pattern
    )

    $match = Select-String -LiteralPath $Path -Pattern $Pattern | Select-Object -First 1
    return $(if ($match) { $match.LineNumber } else { 1 })
}

function Get-EvaluatedProject {
    param(
        [string]$ProjectPath,
        [switch]$ForNet10
    )

    $arguments = @(
        "msbuild",
        $ProjectPath,
        "-nologo",
        "-getProperty:TargetFramework",
        "-getProperty:TargetFrameworks",
        "-getProperty:ManagePackageVersionsCentrally",
        "-getProperty:OrleansDocumentationVersionException",
        "-getItem:PackageReference",
        "-getItem:PackageVersion",
        "-getItem:Compile",
        "-getItem:ProjectReference"
    )
    if ($ForNet10) {
        $arguments += "-property:TargetFramework=net10.0"
    }
    $output = @(& dotnet @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild evaluation failed with exit code $LASTEXITCODE.`n$($output -join "`n")"
    }
    try {
        return ($output -join "`n") | ConvertFrom-Json
    }
    catch {
        throw "MSBuild returned invalid evaluation JSON.`n$($output -join "`n")"
    }
}

$pathComparer = $(if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal })
$compiledSources = [Collections.Generic.HashSet[string]]::new($pathComparer)
$projectReferences = [Collections.Generic.Dictionary[string, string[]]]::new($pathComparer)
$policyIssues = @()

function Test-ContainedPath {
    param(
        [string]$Root,
        [string]$Target
    )

    $relative = [IO.Path]::GetRelativePath($Root, $Target)
    return -not [IO.Path]::IsPathRooted($relative) -and
        $relative -notmatch '^\.\.(?:[\\/]|$)'
}

function Resolve-PhysicalPath {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    $current = $pathRoot
    $relative = [IO.Path]::GetRelativePath($pathRoot, $fullPath)
    foreach ($segment in @($relative -split '[\\/]' | Where-Object { $_ -and $_ -ne "." })) {
        $current = [IO.Path]::Combine($current, $segment)
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $resolved = $item.ResolveLinkTarget($true)
            if (-not $resolved) {
                throw "Could not resolve link '$current'."
            }
            $current = $resolved.FullName
        }
    }
    return [IO.Path]::GetFullPath($current)
}

function Test-IsOlderVersion {
    param(
        [string]$Version,
        [string]$RequiredVersion
    )

    $actual = $null
    $required = $null
    return [Version]::TryParse(($Version -split '[-+]')[0], [ref]$actual) -and
        [Version]::TryParse(($RequiredVersion -split '[-+]')[0], [ref]$required) -and
        $actual -lt $required
}

$physicalValidationRoot = Resolve-PhysicalPath -Path $validationRoot

foreach ($project in $snippetProjects) {
    $projectValid = $true
    $relativePath = [IO.Path]::GetRelativePath($validationRoot, $project)
    try {
        $evaluation = Get-EvaluatedProject -ProjectPath $project
    }
    catch {
        $policyIssues += "${relativePath}:1 [SNIPPET000] Could not evaluate the project with MSBuild. Remediation: fix project/import evaluation errors. $($_.Exception.Message)"
        continue
    }
    $frameworks = @($evaluation.Properties.TargetFramework, $evaluation.Properties.TargetFrameworks) |
        Where-Object { $_ } |
        ForEach-Object { $_ -split ";" } |
        ForEach-Object { $_.Trim() }
    if ($frameworks -notcontains "net10.0") {
        $line = Get-LineNumber -Path $project -Pattern "<TargetFramework"
        $policyIssues += "${relativePath}:${line} [SNIPPET001] Snippet projects must target net10.0. Remediation: set TargetFramework to net10.0 or include net10.0 in TargetFrameworks."
        continue
    }

    if ($evaluation.Properties.TargetFrameworks) {
        try {
            $evaluation = Get-EvaluatedProject -ProjectPath $project -ForNet10
        }
        catch {
            $policyIssues += "${relativePath}:1 [SNIPPET000] Could not evaluate the net10.0 target with MSBuild. Remediation: fix target-specific project/import evaluation errors. $($_.Exception.Message)"
            continue
        }
    }

    $projectReferences[$project] = @($evaluation.Items.ProjectReference |
        Where-Object { $_.FullPath } |
        ForEach-Object { [IO.Path]::GetFullPath([string]$_.FullPath) })

    $exceptionReason = ([string]$evaluation.Properties.OrleansDocumentationVersionException).Trim()
    $isMigrationProject = $relativePath -split '[\\/]' -contains 'migration'
    $validException = $isMigrationProject -and $exceptionReason.Length -ge 20
    $exceptionUsed = $false
    if ($exceptionReason -and -not $isMigrationProject) {
        $policyIssues += "${relativePath}:1 [SNIPPET002] OrleansDocumentationVersionException is restricted to migration projects. Remediation: remove the exception or move the intentional historical example under migration."
        $projectValid = $false
    }
    elseif ($exceptionReason -and $exceptionReason.Length -lt 20) {
        $policyIssues += "${relativePath}:1 [SNIPPET002] OrleansDocumentationVersionException is missing a meaningful reason. Remediation: explain the migration scenario which requires the historical Orleans release."
        $projectValid = $false
    }

    foreach ($reference in @($evaluation.Items.PackageReference)) {
        $packageName = [string]$reference.Identity
        if (-not $packageName.StartsWith("Microsoft.Orleans", [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $centralVersion = $null
        if ($evaluation.Properties.ManagePackageVersionsCentrally -eq "true") {
            $centralVersion = @($evaluation.Items.PackageVersion) |
                Where-Object { $_.Identity -eq $packageName } |
                Select-Object -First 1
        }
        $versionSource = $(if ($reference.VersionOverride -or $reference.Version) { $reference } else { $centralVersion })
        $version = $(if ($reference.VersionOverride) { [string]$reference.VersionOverride } elseif ($reference.Version) { [string]$reference.Version } else { [string]$centralVersion.Version })
        if (
            $version -ne $requiredOrleansVersion -and
            -not ($validException -and (Test-IsOlderVersion -Version $version -RequiredVersion $requiredOrleansVersion))
        ) {
            $definitionPath = $(if ($versionSource -and (Test-Path -LiteralPath $versionSource.DefiningProjectFullPath)) { [string]$versionSource.DefiningProjectFullPath } else { $project })
            $line = Get-LineNumber -Path $definitionPath -Pattern ([regex]::Escape($packageName))
            $definitionRelative = [IO.Path]::GetRelativePath($validationRoot, $definitionPath)
            $displayVersion = $(if ($version) { $version } else { "(missing)" })
            $policyIssues += "${definitionRelative}:${line} [SNIPPET002] Orleans package '$packageName' evaluates to version '$displayVersion' for net10.0. Remediation: update PackageReference, PackageVersion, imported Update, or VersionOverride metadata to exactly $requiredOrleansVersion; an older migration snippet requires a meaningful OrleansDocumentationVersionException project property."
            $projectValid = $false
        }
        elseif ($version -ne $requiredOrleansVersion) {
            $exceptionUsed = $true
        }
    }
    if ($validException -and -not $exceptionUsed) {
        $policyIssues += "${relativePath}:1 [SNIPPET002] OrleansDocumentationVersionException is stale because no older Orleans package reference uses it. Remediation: remove the exception or restore the intentional historical package reference."
        $projectValid = $false
    }

    if ($projectValid) {
        foreach ($compileItem in @($evaluation.Items.Compile)) {
            $compilePath = [string]$compileItem.FullPath
            if (-not $compilePath) {
                $compilePath = [IO.Path]::GetFullPath(
                    [IO.Path]::Combine((Split-Path -Parent $project), [string]$compileItem.Identity)
                )
            }
            [void]$compiledSources.Add($compilePath)
        }
    }
}

function Get-DirectiveAttributes {
    param([string]$AttributeSource)

    $attributes = @{}
    $matches = [regex]::Matches($AttributeSource, '([\w-]+)="([^"]*)"')
    foreach ($match in $matches) {
        $attributes[$match.Groups[1].Value] = $match.Groups[2].Value
    }
    $remainder = [regex]::Replace($AttributeSource, '([\w-]+)="([^"]*)"', '')
    if ($remainder.Trim()) {
        throw "Unsupported directive syntax '$($remainder.Trim())'."
    }
    return $attributes
}

$defaultValidationRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$siteRoot = $(if ($SiteRootPath) {
    (Resolve-Path -LiteralPath $SiteRootPath).Path
} elseif ($validationRoot -eq $defaultValidationRoot) {
    Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $validationRoot))
} else {
    $validationRoot
})
$resolverPath = [IO.Path]::GetFullPath(
    [IO.Path]::Combine($scriptDir, "../../../scripts/resolve-rendered-markdown.mjs")
)
$resolverOutput = @(& node $resolverPath $validationRoot $siteRoot 2>&1)
if ($LASTEXITCODE -ne 0) {
    $policyIssues += ".:1 [SNIPPET003] Could not resolve the rendered Markdown include closure. Remediation: fix missing, circular, malformed, or out-of-tree INCLUDE directives. $($resolverOutput -join "`n")"
    $renderedMarkdown = @()
} else {
    try {
        $renderedMarkdown = @(($resolverOutput -join "`n") | ConvertFrom-Json)
    }
    catch {
        $policyIssues += ".:1 [SNIPPET003] Include closure resolver returned invalid JSON. Remediation: fix the Node documentation tooling. $($_.Exception.Message)"
        $renderedMarkdown = @()
    }
}

foreach ($markdownDocument in $renderedMarkdown) {
    $markdownFile = Get-Item -LiteralPath ([string]$markdownDocument.path)
    $protectedLineRanges = @($markdownDocument.protectedLineRanges)
    $lines = @(Get-Content -LiteralPath $markdownFile.FullName)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $lineNumber = $index + 1
        $isProtected = @($protectedLineRanges | Where-Object {
            $lineNumber -ge [int]$_[0] -and $lineNumber -le [int]$_[1]
        }).Count -gt 0
        if ($isProtected) {
            continue
        }
        $line = $lines[$index]
        $directive = [regex]::Match($line, '^\s*:::code\s+(.+?)\s*$')
        if (-not $directive.Success) {
            if ($line.Contains(":::code")) {
                $directiveRelative = [IO.Path]::GetRelativePath($siteRoot, $markdownFile.FullName)
                $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Unsupported code directive syntax. Remediation: use a single-line :::code directive with quoted attributes."
            }
            continue
        }

        $directiveRelative = [IO.Path]::GetRelativePath($siteRoot, $markdownFile.FullName)
        $attributeSource = $directive.Groups[1].Value
        if ($attributeSource.EndsWith(":::")) {
            $attributeSource = $attributeSource.Substring(0, $attributeSource.Length - 3).TrimEnd()
        }
        try {
            $attributes = Get-DirectiveAttributes -AttributeSource $attributeSource
        }
        catch {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] $($_.Exception.Message) Remediation: use supported quoted attributes."
            continue
        }
        if (-not $attributes.source) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive is missing its source attribute. Remediation: reference an existing snippet source file."
            continue
        }

        $requestedSource = [string]$attributes.source
        $allowedBoundary = $validationRoot
        if ([IO.Path]::IsPathRooted($requestedSource)) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive target '$requestedSource' must be relative and remain within allowed snippet root '$allowedBoundary'. Remediation: reference a file within the allowed snippet root."
            continue
        }

        $target = [IO.Path]::GetFullPath(
            [IO.Path]::Combine($markdownFile.DirectoryName, $requestedSource)
        )
        if (-not (Test-ContainedPath -Root $validationRoot -Target $target)) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive target '$requestedSource' resolves outside allowed snippet root '$allowedBoundary'. Remediation: reference a file within the allowed snippet root."
            continue
        }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive target '$requestedSource' does not exist. Allowed snippet root: '$allowedBoundary'. Remediation: correct the source path or add the target file within the allowed snippet root."
            continue
        }

        try {
            $physicalTarget = Resolve-PhysicalPath -Path $target
        }
        catch {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive target '$requestedSource' could not be resolved safely within allowed snippet root '$allowedBoundary'. Remediation: remove the link or reference a regular file. $($_.Exception.Message)"
            continue
        }
        if (-not (Test-ContainedPath -Root $physicalValidationRoot -Target $physicalTarget)) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] Code directive target '$requestedSource' resolves through a link outside allowed snippet root '$allowedBoundary'. Remediation: reference a regular file within the allowed snippet root."
            continue
        }

        $language = ([string]$attributes.language).Trim()
        $isCsharp = [IO.Path]::GetExtension($target) -ieq ".cs" -or
            $language -ieq "csharp" -or
            $language -ieq "c#" -or
            $language -ieq "cs"
        if (
            $isCsharp -and
            -not $attributes.id
        ) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] C# code directive target '$($attributes.source)' does not specify an id. Remediation: reference a named region so documentation and hidden scaffolding remain separate."
        }
        if ($isCsharp -and -not $compiledSources.Contains($target)) {
            $policyIssues += "${directiveRelative}:$($index + 1) [SNIPPET003] C# code directive target '$($attributes.source)' is not an evaluated Compile item in any validated net10.0 Orleans 10 snippet project. Remediation: include the file in a validated snippet project (linked Compile items are supported) or use a non-C# source."
        }
    }
}

if ($policyIssues.Count -gt 0) {
    Write-Host "Snippet policy validation failed:" -ForegroundColor Red
    $policyIssues | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

if ($PolicyOnly) {
    Write-Host "Snippet policy validation passed for $($snippetProjects.Count) project(s)." -ForegroundColor Green
    exit 0
}

$results = @()
$failCount = 0
$successCount = 0

function Invoke-ProjectValidation {
    param(
        [string]$ProjectPath,
        [bool]$IsTestProject
    )
    
    $relativePath = [IO.Path]::GetRelativePath($validationRoot, $ProjectPath)
    $command = if ($IsTestProject) { "test" } else { "build" }
    $action = if ($IsTestProject) { "Testing" } else { "Building" }
    
    Write-Host "${action}: $relativePath" -ForegroundColor Yellow -NoNewline
    
    $output = & dotnet $command $ProjectPath --framework net10.0 --nologo -v q 2>&1
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-Host " [OK]" -ForegroundColor Green
        return @{
            Project = $relativePath
            Action = $command
            Success = $true
            Output = $output -join "`n"
        }
    } else {
        Write-Host " [FAILED]" -ForegroundColor Red
        return @{
            Project = $relativePath
            Action = $command
            Success = $false
            Output = $output -join "`n"
        }
    }
}

function Get-EvaluatedProjectReferences {
    param([string]$ProjectPath)

    $arguments = @(
        "msbuild",
        $ProjectPath,
        "-nologo",
        "-getItem:ProjectReference",
        "-property:TargetFramework=net10.0"
    )
    $output = @(& dotnet @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild project-reference evaluation failed with exit code $LASTEXITCODE.`n$($output -join "`n")"
    }

    try {
        $evaluation = ($output -join "`n") | ConvertFrom-Json
    }
    catch {
        throw "MSBuild returned invalid project-reference evaluation JSON.`n$($output -join "`n")"
    }

    return @($evaluation.Items.ProjectReference |
        Where-Object { $_.FullPath } |
        ForEach-Object { [IO.Path]::GetFullPath([string]$_.FullPath) })
}

function Get-ProjectReferenceClosure {
    param([string]$ProjectPath)

    $closure = [Collections.Generic.HashSet[string]]::new($pathComparer)
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue([IO.Path]::GetFullPath($ProjectPath))

    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        if (-not $closure.Add($current)) {
            continue
        }

        if (-not $projectReferences.ContainsKey($current)) {
            if (Test-Path -LiteralPath $current -PathType Leaf) {
                $projectReferences[$current] = @(Get-EvaluatedProjectReferences -ProjectPath $current)
            } else {
                $projectReferences[$current] = @()
            }
        }

        foreach ($reference in $projectReferences[$current]) {
            $pending.Enqueue($reference)
        }
    }

    return @($closure)
}

function Get-ParallelValidationBatches {
    param([string[]]$ProjectPaths)

    $batches = [Collections.Generic.List[object]]::new()
    foreach ($project in $ProjectPaths) {
        $closure = @(Get-ProjectReferenceClosure -ProjectPath $project)
        $selectedBatch = $null

        foreach ($batch in $batches) {
            $overlaps = $false
            foreach ($path in $closure) {
                if ($batch.Closure.Contains($path)) {
                    $overlaps = $true
                    break
                }
            }

            if (-not $overlaps) {
                $selectedBatch = $batch
                break
            }
        }

        if (-not $selectedBatch) {
            $selectedBatch = [pscustomobject]@{
                Projects = [Collections.Generic.List[string]]::new()
                Closure = [Collections.Generic.HashSet[string]]::new($pathComparer)
            }
            [void]$batches.Add($selectedBatch)
        }

        [void]$selectedBatch.Projects.Add($project)
        foreach ($path in $closure) {
            [void]$selectedBatch.Closure.Add($path)
        }
    }

    return $batches
}

if ($Parallel) {
    try {
        $parallelBatches = @(Get-ParallelValidationBatches -ProjectPaths $snippetProjects)
    }
    catch {
        Write-Host "Could not build the project-reference graph required for parallel validation." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 1
    }

    Write-Host "Running validations in $($parallelBatches.Count) dependency-safe parallel batch(es)..." -ForegroundColor Cyan
    for ($batchIndex = 0; $batchIndex -lt $parallelBatches.Count; $batchIndex++) {
        $batch = $parallelBatches[$batchIndex]
        Write-Host "  Batch $($batchIndex + 1): $($batch.Projects.Count) project(s)" -ForegroundColor Gray
        $batchResults = $batch.Projects | ForEach-Object -Parallel {
            $ProjectPath = $_
            $validationRoot = $using:validationRoot
            $relativePath = [IO.Path]::GetRelativePath($validationRoot, $ProjectPath)
            [xml] $projectXml = Get-Content -LiteralPath $ProjectPath -Raw
            $isTestProject = $projectXml.Project.PropertyGroup.IsTestProject -contains "true"
            $command = if ($isTestProject) { "test" } else { "build" }

            $output = & dotnet $command $ProjectPath --framework net10.0 --nologo -v q 2>&1
            $exitCode = $LASTEXITCODE

            @{
                Project = $relativePath
                Action = $command
                Success = ($exitCode -eq 0)
                Output = $output -join "`n"
            }
        } -ThrottleLimit 4
        $results += @($batchResults)
    }
} else {
    foreach ($project in $snippetProjects) {
        $result = Invoke-ProjectValidation -ProjectPath $project -IsTestProject (Test-IsTestProject -ProjectPath $project)
        $results += $result
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$successCount = ($results | Where-Object { $_.Success }).Count
$failCount = ($results | Where-Object { -not $_.Success }).Count

Write-Host "Succeeded: $successCount" -ForegroundColor Green
Write-Host "Failed:    $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Green" })
Write-Host ""

# Show details for failed validations
$failed = $results | Where-Object { -not $_.Success }
if ($failed.Count -gt 0) {
    Write-Host "Failed Projects:" -ForegroundColor Red
    Write-Host "----------------" -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host ""
        Write-Host "Project: $($f.Project) ($($f.Action))" -ForegroundColor Red
        Write-Host "Output:" -ForegroundColor Yellow
        Write-Host $f.Output
    }
    Write-Host ""
    exit 1
}

Write-Host "All snippet projects validated successfully!" -ForegroundColor Green
exit 0
