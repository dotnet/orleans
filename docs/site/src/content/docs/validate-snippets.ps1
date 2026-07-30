<#
.SYNOPSIS
    Validates that all Orleans documentation snippet projects compile successfully.

.DESCRIPTION
    This script finds all .csproj files in the snippets directories under docs/orleans/
    and runs 'dotnet build' on each project to verify they compile.
    
    Use this script to validate snippet code after making changes to ensure all
    documentation examples remain buildable.

.PARAMETER Parallel
    Run builds in parallel (default: false for clearer output)

.EXAMPLE
    .\validate-snippets.ps1
    
    Builds all snippet projects sequentially and reports results.

.EXAMPLE
    .\validate-snippets.ps1 -Parallel
    
    Builds all snippet projects in parallel for faster validation.

.NOTES
    Exit codes:
    0 - All projects built successfully
    1 - One or more projects failed to build
#>

param(
    [switch]$Parallel = $false
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Orleans Documentation Snippet Validator" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find all .csproj files in snippets directories
$snippetProjects = Get-ChildItem -Path $scriptDir -Recurse -Filter "*.csproj" |
    Where-Object { $_.FullName -match "snippets" } |
    Select-Object -ExpandProperty FullName

if ($snippetProjects.Count -eq 0) {
    Write-Host "No snippet projects found!" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($snippetProjects.Count) snippet project(s) to validate:" -ForegroundColor Green
$snippetProjects | ForEach-Object { 
    $relativePath = $_.Replace($scriptDir, "").TrimStart("\", "/")
    Write-Host "  - $relativePath" -ForegroundColor Gray
}
Write-Host ""

$results = @()
$failCount = 0
$successCount = 0

function Build-Project {
    param([string]$ProjectPath)
    
    $relativePath = $ProjectPath.Replace($scriptDir, "").TrimStart("\", "/")
    $projectDir = Split-Path -Parent $ProjectPath
    
    Write-Host "Building: $relativePath" -ForegroundColor Yellow -NoNewline
    
    $output = & dotnet build $ProjectPath --nologo -v q 2>&1
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-Host " [OK]" -ForegroundColor Green
        return @{
            Project = $relativePath
            Success = $true
            Output = $output -join "`n"
        }
    } else {
        Write-Host " [FAILED]" -ForegroundColor Red
        return @{
            Project = $relativePath
            Success = $false
            Output = $output -join "`n"
        }
    }
}

if ($Parallel) {
    Write-Host "Running builds in parallel..." -ForegroundColor Cyan
    $results = $snippetProjects | ForEach-Object -Parallel {
        $ProjectPath = $_
        $scriptDir = $using:scriptDir
        $relativePath = $ProjectPath.Replace($scriptDir, "").TrimStart("\", "/")
        
        $output = & dotnet build $ProjectPath --nologo -v q 2>&1
        $exitCode = $LASTEXITCODE
        
        @{
            Project = $relativePath
            Success = ($exitCode -eq 0)
            Output = $output -join "`n"
        }
    } -ThrottleLimit 4
} else {
    foreach ($project in $snippetProjects) {
        $result = Build-Project -ProjectPath $project
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

# Show details for failed builds
$failed = $results | Where-Object { -not $_.Success }
if ($failed.Count -gt 0) {
    Write-Host "Failed Projects:" -ForegroundColor Red
    Write-Host "----------------" -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host ""
        Write-Host "Project: $($f.Project)" -ForegroundColor Red
        Write-Host "Output:" -ForegroundColor Yellow
        Write-Host $f.Output
    }
    Write-Host ""
    exit 1
}

Write-Host "All snippet projects built successfully!" -ForegroundColor Green
exit 0
