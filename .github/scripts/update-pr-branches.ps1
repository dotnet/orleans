#Requires -Version 7.0
<#
.SYNOPSIS
Updates open GitHub pull request branches using GitHub's server-side rebase.
.DESCRIPTION
Defaults to dotnet/orleans and the main base branch. Includes all open pull
requests targeting BaseBranch, including drafts. Updates branches which are
behind and which GitHub reports as mergeable and rebaseable.
GitHub CLI supplies the expected head SHA and GitHub enforces update permissions
and conflict checks. Resolves BaseBranch from the repository's Git ref once per
run and uses that snapshot to assess every PR and confirm each successful update.
Emits one result object per pull request and a final summary grouped by outcome,
with merge and rebase conflicts first. Fails after reporting any errors.
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf
.EXAMPLE
.\update-pr-branches.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository = 'dotnet/orleans',

    [ValidateNotNullOrEmpty()]
    [string] $BaseBranch = 'main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Gh {
    param([string[]] $Arguments)

    $originalEncoding = [Console]::OutputEncoding
    try {
        # PowerShell decodes native output using the console encoding; gh emits UTF-8.
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $output = & gh @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        [Console]::OutputEncoding = $originalEncoding
    }

    if ($exitCode -ne 0) {
        throw [InvalidOperationException]::new(
            "gh $($Arguments -join ' ') failed (exit ${exitCode}): $($output -join [Environment]::NewLine)")
    }

    return $output
}

function Get-GitHubJson {
    param([string] $Endpoint)

    $output = Invoke-Gh -Arguments @('api', '--hostname', 'github.com', '--method', 'GET', $Endpoint)
    return ($output -join [Environment]::NewLine | ConvertFrom-Json)
}

function Get-PullRequest {
    param([int] $Number)

    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $pull = Get-GitHubJson "repos/$Repository/pulls/$Number"
        if ($pull.state -ne 'open' -or $pull.base.ref -ne $BaseBranch -or
            $pull.mergeable -eq $false -or
            ($null -ne $pull.mergeable -and $null -ne $pull.rebaseable)) {
            return $pull
        }

        # GitHub computes mergeability asynchronously after the first request.
        if ($attempt -lt 4) {
            Start-Sleep -Seconds 2
        }
    }

    return $pull
}

function Get-BehindCount {
    param([string] $BaseSha, [string] $HeadSha)

    $comparison = Get-GitHubJson "repos/$Repository/compare/$BaseSha...${HeadSha}?per_page=1"
    return $comparison.behind_by
}

function Write-ResultSummary {
    param([object[]] $Results)

    $groups = [ordered]@{
        MergeConflict = 'Merge conflicts'
        RebaseConflict = 'Rebase conflicts'
        Failed = 'Failed updates'
        Eligible = 'Eligible for rebase (preview)'
        Updated = 'Updated'
        UpToDate = 'Up to date'
        Skipped = 'Other skipped PRs'
    }

    Write-Host "`nResults summary"
    foreach ($group in $groups.GetEnumerator()) {
        $items = @($Results | Where-Object Status -eq $group.Key | Sort-Object Number)
        Write-Host "`n$($group.Value): $($items.Count)"
        foreach ($item in $items) {
            Write-Host "  $($item.Url) - $($item.Title)"
            if ($group.Key -in 'Failed', 'Skipped') {
                Write-Host "    $($item.Message)"
            }
        }
    }
}

$helpText = Invoke-Gh -Arguments @('pr', 'update-branch', '--help')
if (($helpText -join [Environment]::NewLine) -notmatch '--rebase') {
    throw 'Install a GitHub CLI version which supports gh pr update-branch --rebase.'
}

$encodedBase = [Uri]::EscapeDataString($BaseBranch)
$baseRef = Get-GitHubJson "repos/$Repository/git/ref/heads/$encodedBase"
$baseSha = $baseRef.object.sha
Write-Host "Using $Repository branch $BaseBranch at $baseSha as this run's baseline."
$numbers = @(Invoke-Gh -Arguments @(
    'api', '--hostname', 'github.com', '--method', 'GET', '--paginate',
    "repos/$Repository/pulls?state=open&base=$encodedBase&per_page=100", '--jq', '.[].number'
))
$results = [Collections.Generic.List[object]]::new()

foreach ($number in $numbers) {
    $result = [pscustomobject][ordered]@{
        Number = [int] $number
        Url = "https://github.com/$Repository/pull/$number"
        Title = $null
        HeadRepository = $null
        HeadBranch = $null
        BaseSha = $baseSha
        PreviousHeadSha = $null
        HeadSha = $null
        BehindBy = $null
        Status = 'Skipped'
        Message = $null
    }

    try {
        $pull = Get-PullRequest -Number $number
        $result.Title = $pull.title
        $result.HeadBranch = $pull.head.ref
        $result.PreviousHeadSha = $pull.head.sha
        $result.HeadSha = $pull.head.sha

        if ($pull.state -ne 'open' -or $pull.base.ref -ne $BaseBranch) {
            $result.Message = 'PR state or target branch changed.'
        } elseif ($null -eq $pull.head.repo) {
            $result.Message = 'Head repository is unavailable.'
        } else {
            $result.HeadRepository = $pull.head.repo.full_name
            $result.BehindBy = Get-BehindCount -BaseSha $result.BaseSha -HeadSha $pull.head.sha
            if ($result.BehindBy -eq 0) {
                $result.Status = 'UpToDate'
                $result.Message = "Branch contains this run's $BaseBranch snapshot."
            } elseif ($pull.mergeable -eq $false) {
                $result.Status = 'MergeConflict'
                $result.Message = 'GitHub reports merge conflicts.'
            } elseif ($pull.rebaseable -eq $false) {
                $result.Status = 'RebaseConflict'
                $result.Message = 'GitHub reports that rebase requires conflict resolution.'
            } elseif ($pull.mergeable -ne $true -or $pull.rebaseable -ne $true) {
                $result.Message = 'GitHub is still calculating mergeability after five reads.'
            } elseif ($PSCmdlet.ShouldProcess(
                "$Repository#$number ($($result.HeadRepository):$($result.HeadBranch))",
                "Update with rebase onto $BaseBranch ($($result.BehindBy) commits behind)")) {
                $updateOutput = Invoke-Gh -Arguments @(
                    'pr', 'update-branch', [string] $number, '--repo', "github.com/$Repository", '--rebase'
                )

                for ($attempt = 0; $attempt -lt 5; $attempt++) {
                    $updated = Get-GitHubJson "repos/$Repository/pulls/$number"
                    $result.HeadSha = $updated.head.sha
                    $result.BehindBy = Get-BehindCount -BaseSha $result.BaseSha -HeadSha $result.HeadSha
                    if ($result.BehindBy -eq 0) {
                        break
                    }

                    if ($attempt -lt 4) {
                        Start-Sleep -Seconds 2
                    }
                }

                if ($result.BehindBy -ne 0) {
                    throw [InvalidOperationException]::new(
                        "GitHub accepted the update, but the branch is still behind base $($result.BaseSha) after five reads.")
                }

                $result.Status = 'Updated'
                $result.Message = ($updateOutput -join [Environment]::NewLine).Trim()
            } else {
                $result.Status = if ($WhatIfPreference) { 'Eligible' } else { 'Skipped' }
                $result.Message = if ($WhatIfPreference) { 'Preview: ready for rebase.' } else { 'Update declined.' }
            }
        }
    } catch [InvalidOperationException] {
        $result.Status = 'Failed'
        $result.Message = $_.Exception.Message
        Write-Warning "#${number}: $($result.Message)"
    }

    Write-Host "#${number}: $($result.Status) - $($result.Message)"
    $results.Add($result)
    $result
}

Write-Host "Processed $($results.Count) pull requests in $Repository targeting $BaseBranch."
Write-ResultSummary -Results $results

if (@($results | Where-Object Status -eq 'Failed').Count -gt 0) {
    throw 'One or more pull request updates failed. See the per-PR results above.'
}
