Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cacheAction = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../actions/cache-dependencies/action.yml')
$ciWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../workflows/ci.yml')

$defaultScope = [regex]::Match($cacheAction, '(?m)^  npm-scope:\r?\n(?:    .*\r?\n)*?    default: (\S+)')
if (-not $defaultScope.Success -or $defaultScope.Groups[1].Value -ne 'dashboard') {
    throw 'The dashboard download store must have a default npm consumer scope.'
}

$npmKey = [regex]::Match($cacheAction, '(?m)^      key: (npm-v\d+-[^\r\n]+)')
if (-not $npmKey.Success -or -not $npmKey.Groups[1].Value.Contains('${{ inputs.npm-scope }}')) {
    throw 'The npm cache key must include the consumer scope.'
}

$googleJob = [regex]::Match($ciWorkflow, '(?ms)^  test-google:\r?\n.*?(?=^  [\w-]+:|\z)')
$googleCache = [regex]::Match($googleJob.Value, '(?ms)^    - name: Cache npm downloads\r?\n.*?(?=^    - |\z)')
$firebaseScope = [regex]::Match($googleCache.Value, '(?m)^        npm-scope: (\S+)')
if (-not $firebaseScope.Success -or $firebaseScope.Groups[1].Value -ne 'firebase') {
    throw 'The Google job must use the Firebase npm consumer scope.'
}

$dashboardKey = $npmKey.Groups[1].Value.Replace('${{ inputs.npm-scope }}', $defaultScope.Groups[1].Value)
$firebaseKey = $npmKey.Groups[1].Value.Replace('${{ inputs.npm-scope }}', $firebaseScope.Groups[1].Value)
if ($dashboardKey -eq $firebaseKey) {
    throw 'Dashboard and Firebase downloads must have distinct cache keys with identical tools and manifests.'
}

Write-Output 'PASS NpmConsumerCachesHaveDistinctKeys'
