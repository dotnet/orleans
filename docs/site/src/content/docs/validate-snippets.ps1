<#
.SYNOPSIS
    Validates all Orleans documentation snippet projects.

.DESCRIPTION
    This script finds all .csproj files in the documentation snippets directories
    and runs 'dotnet build' on ordinary projects or 'dotnet test' on projects which
    declare IsTestProject=true.
    
    Use this script to validate snippet code after making changes to ensure all
    documentation examples remain buildable and executable test examples pass.

.PARAMETER Parallel
    Run validations in parallel (default: false for clearer output)

.EXAMPLE
    .\validate-snippets.ps1
    
    Validates all snippet projects sequentially and reports results.

.EXAMPLE
    .\validate-snippets.ps1 -Parallel
    
    Validates all snippet projects in parallel for faster validation.

.NOTES
    Exit codes:
    0 - All projects validated successfully
    1 - One or more projects failed validation
#>

param(
    [switch]$Parallel = $false
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Orleans Documentation Snippet Validator" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find all .csproj files in snippets directories and identify executable test projects.
$snippetProjects = Get-ChildItem -Path $scriptDir -Recurse -Filter "*.csproj" |
    Where-Object { $_.FullName -match "snippets" } |
    ForEach-Object {
        [xml] $projectXml = Get-Content -Path $_.FullName -Raw
        $isTestProject = $projectXml.Project.PropertyGroup.IsTestProject -contains "true"
        [pscustomobject]@{
            Path = $_.FullName
            IsTestProject = $isTestProject
        }
    }

if ($snippetProjects.Count -eq 0) {
    Write-Host "No snippet projects found!" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($snippetProjects.Count) snippet project(s) to validate:" -ForegroundColor Green
$snippetProjects | ForEach-Object { 
    $relativePath = $_.Path.Replace($scriptDir, "").TrimStart("\", "/")
    $action = if ($_.IsTestProject) { "test" } else { "build" }
    Write-Host "  - $relativePath ($action)" -ForegroundColor Gray
}
Write-Host ""

$results = @()
$failCount = 0
$successCount = 0

function Invoke-ProjectValidation {
    param(
        [string]$ProjectPath,
        [bool]$IsTestProject
    )
    
    $relativePath = $ProjectPath.Replace($scriptDir, "").TrimStart("\", "/")
    $command = if ($IsTestProject) { "test" } else { "build" }
    $action = if ($IsTestProject) { "Testing" } else { "Building" }
    
    Write-Host "${action}: $relativePath" -ForegroundColor Yellow -NoNewline
    
    $output = & dotnet $command $ProjectPath --nologo -v q 2>&1
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

if ($Parallel) {
    Write-Host "Running validations in parallel..." -ForegroundColor Cyan
    $results = $snippetProjects | ForEach-Object -Parallel {
        $ProjectPath = $_.Path
        $IsTestProject = $_.IsTestProject
        $scriptDir = $using:scriptDir
        $relativePath = $ProjectPath.Replace($scriptDir, "").TrimStart("\", "/")
        $command = if ($IsTestProject) { "test" } else { "build" }
        
        $output = & dotnet $command $ProjectPath --nologo -v q 2>&1
        $exitCode = $LASTEXITCODE
        
        @{
            Project = $relativePath
            Action = $command
            Success = ($exitCode -eq 0)
            Output = $output -join "`n"
        }
    } -ThrottleLimit 4
} else {
    foreach ($project in $snippetProjects) {
        $result = Invoke-ProjectValidation -ProjectPath $project.Path -IsTestProject $project.IsTestProject
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
