param(
    [switch]$Help,
    [int]$MaxIterations = 0
)

function Show-Usage {
    @"
Usage: ./.opencode/.ralph/ralph.ps1 [-Help] [-MaxIterations <int>]

  -Help               Show this message
  -MaxIterations      Max iterations before force stop (0 = infinite, default: 0)

The script monitors research/feature-list.json and iterates until all entries have "passes": true.
If -MaxIterations > 0, the script will stop after that many iterations regardless of feature status.
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

$FeatureListPath = "research/feature-list.json"

# Check if feature-list.json exists in research folder
if (-not (Test-Path $FeatureListPath -PathType Leaf)) {
    Write-Host "ERROR: feature-list.json not found in research folder." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please run the /create-feature-list command first to generate the feature list." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

function Test-AllFeaturesPassing {
    param(
        [string]$Path
    )

    if (-not (Test-Path $Path -PathType Leaf)) {
        return $false
    }

    try {
        $features = Get-Content $Path -Raw | ConvertFrom-Json
        
        if ($features.Count -eq 0) {
            Write-Host "ERROR: research/feature-list.json is empty." -ForegroundColor Red
            return $false
        }

        $totalFeatures = $features.Count
        $passingFeatures = ($features | Where-Object { $_.passes -eq $true }).Count
        $failingFeatures = $totalFeatures - $passingFeatures

        Write-Host "Feature Progress: $passingFeatures / $totalFeatures passing ($failingFeatures remaining)" -ForegroundColor Cyan

        return $failingFeatures -eq 0
    }
    catch {
        Write-Host "ERROR: Failed to parse research/feature-list.json: $_" -ForegroundColor Red
        return $false
    }
}

Write-Host "Starting ralph - monitoring research/feature-list.json for completion..." -ForegroundColor Green
if ($MaxIterations -gt 0) {
    Write-Host "Max iterations set to $MaxIterations" -ForegroundColor Yellow
}
Write-Host ""

$iteration = 0
while ($true) {
    $iteration++
    
    if ($MaxIterations -gt 0) {
        Write-Host "Iteration: $iteration / $MaxIterations"
    } else {
        Write-Host "Iteration: $iteration"
    }
    
    & ./.opencode/.ralph/sync.ps1
    
    if (Test-AllFeaturesPassing -Path $FeatureListPath) {
        Write-Host "All features passing! Exiting loop." -ForegroundColor Green
        break
    }
    
    if ($MaxIterations -gt 0 -and $iteration -ge $MaxIterations) {
        Write-Host "Max iterations ($MaxIterations) reached. Force stopping." -ForegroundColor Yellow
        break
    }
    
    Write-Host "===SLEEP===`n===SLEEP===`n"
    Write-Host "looping"
    Start-Sleep -Seconds 10
}
