[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string] $Manifest,

    [Parameter(Mandatory)]
    [string] $JsonOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maximumInputBytes = 10MB

function Assert-NotReparsePoint {
    param([string] $Path)

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$resolvedManifest = (Resolve-Path -LiteralPath $Manifest).Path
Assert-NotReparsePoint $resolvedRepositoryRoot
Assert-NotReparsePoint $resolvedManifest

$inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in [IO.File]::ReadAllLines($resolvedManifest)) {
    $relativePath = $line.Trim()
    if (-not $relativePath) {
        continue
    }

    $parts = $relativePath.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if ($relativePath -ne ($parts -join '/') -or
        $parts.Count -eq 0 -or
        $parts.Where({ $_ -in '.', '..' }, 'First').Count -gt 0 -or
        $relativePath -notmatch '^(?:\.github/[A-Za-z0-9._/-]+|global\.json)$') {
        throw "Invalid coverage input path '$relativePath'"
    }
    if (-not $inputs.Add($relativePath)) {
        throw "Duplicate coverage input path '$relativePath'"
    }
}
if ($inputs.Count -eq 0) {
    throw 'The coverage input manifest is empty'
}

$sortedInputs = @($inputs)
[Array]::Sort($sortedInputs, [StringComparer]::Ordinal)
$entries = @(
    foreach ($relativePath in $sortedInputs) {
        $currentPath = $resolvedRepositoryRoot
        Assert-NotReparsePoint $currentPath
        foreach ($part in $relativePath.Split('/')) {
            $currentPath = Join-Path $currentPath $part
            if (-not (Test-Path -LiteralPath $currentPath)) {
                throw "Coverage input '$relativePath' is missing"
            }
            Assert-NotReparsePoint $currentPath
        }

        $inputFile = Get-Item -LiteralPath $currentPath -Force
        if ($inputFile.PSIsContainer) {
            throw "Coverage input '$relativePath' is not a file"
        }
        if ($inputFile.Length -gt $maximumInputBytes) {
            throw "Coverage input '$relativePath' exceeds the 10 MB hashing limit"
        }

        $fileHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($inputFile.FullName))).ToLowerInvariant()
        "$relativePath`0$fileHash"
    }
)
$fingerprintBytes = [Text.UTF8Encoding]::new($false).GetBytes(($entries -join "`n") + "`n")
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fingerprintBytes)).ToLowerInvariant()
$result = [ordered]@{
    files = $inputs.Count
    sha256 = $fingerprint
}
$result | ConvertTo-Json | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM
