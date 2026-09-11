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
Processes each PR's branch update and review feedback together, then prints a
compact summary grouped by branch, review, and CI status, ordered by mergeability.
Current, conflict-free PRs with approval recommended and passing checks appear
first; PRs needing updates, review, CI completion, changes, or conflict resolution
follow. Lists PR numbers, titles, PR links, and status icons. Use -Verbose for
diagnostics and individual thread locations. Returns exit code 1 on failures.
Use -PassThru to emit full result objects. Their Review property contains unresolved
threads (including outdated threads), code suggestion counts, and the latest
Copilot review's state, overview recommendation, URL, reviewed commit, and body.
Review.Checks contains GitHub's aggregate check state and counts for the head commit.
Recommendations are extracted from Copilot's Markdown overview heading.
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf
.EXAMPLE
.\update-pr-branches.ps1
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf -Verbose
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf -PassThru | ConvertTo-Json -Depth 12 | Set-Content pr-review-report.json
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository = 'dotnet/orleans',

    [ValidateNotNullOrEmpty()]
    [string] $BaseBranch = 'main',

    [switch] $PassThru
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

function Get-GraphQLPages {
    param([string] $Query, [string[]] $Fields)

    $output = Invoke-Gh -Arguments (
        @('api', '--hostname', 'github.com', 'graphql', '--paginate', '--slurp', '-f', "query=$Query") + $Fields)
    return ($output -join [Environment]::NewLine | ConvertFrom-Json)
}

function Get-ReviewReport {
    param([int] $Number)

    $owner, $name = $Repository.Split('/')
    $fields = @('-f', "owner=$owner", '-f', "name=$name", '-F', "number=$Number")
    $threadPages = @(Get-GraphQLPages -Fields $fields -Query @'
query($owner: String!, $name: String!, $number: Int!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      headRefOid
      mergeable
      reviewDecision
      commits(last: 1) {
        nodes {
          commit {
            oid
            statusCheckRollup {
              state
              contexts {
                totalCount
                checkRunCountsByState { count state }
                statusContextCountsByState { count state }
              }
            }
          }
        }
      }
      reviewThreads(first: 100, after: $endCursor) {
        nodes {
          id isResolved isOutdated path line
          comments(first: 100) {
            totalCount
            nodes { author { login } body url }
          }
        }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
'@)
    $pull = $threadPages[0].data.repository.pullRequest
    if ($null -eq $pull) {
        throw [InvalidOperationException]::new("Review data for $Repository#$Number is unavailable.")
    }

    $threads = @(
        foreach ($page in $threadPages) {
            foreach ($thread in $page.data.repository.pullRequest.reviewThreads.nodes) {
                if ($thread.isResolved) {
                    continue
                }

                $comments = @($thread.comments.nodes)
                if ($thread.comments.totalCount -gt $comments.Count) {
                    $commentPages = @(Get-GraphQLPages -Fields @('-f', "id=$($thread.id)") -Query @'
query($id: ID!, $endCursor: String) {
  node(id: $id) {
    ... on PullRequestReviewThread {
      comments(first: 100, after: $endCursor) {
        nodes { author { login } body url }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
'@)
                    $comments = @($commentPages | ForEach-Object { $_.data.node.comments.nodes })
                }

                $root = $comments[0]
                $suggestions = 0
                $copilotSuggestions = 0
                foreach ($comment in $comments) {
                    $count = [regex]::Matches($comment.body, '(?m)^[ \t]*```suggestion(?=[ \t:\r\n]|$)').Count
                    $suggestions += $count
                    if ($null -ne $comment.author -and $comment.author.login -eq 'copilot-pull-request-reviewer') {
                        $copilotSuggestions += $count
                    }
                }

                [pscustomobject]@{
                    Url = $root.url
                    Author = if ($null -ne $root.author) { $root.author.login } else { $null }
                    IsCopilot = $null -ne $root.author -and $root.author.login -eq 'copilot-pull-request-reviewer'
                    Path = $thread.path
                    Line = $thread.line
                    IsOutdated = $thread.isOutdated
                    SuggestionCount = $suggestions
                    CopilotSuggestionCount = $copilotSuggestions
                }
            }
        }
    )

    # Fetch all authors: GitHub's review author filter does not reliably match bot accounts.
    $reviewPages = @(Get-GraphQLPages -Fields $fields -Query @'
query($owner: String!, $name: String!, $number: Int!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviews(first: 100, after: $endCursor) {
        nodes { author { login } state submittedAt url commit { oid } body }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
'@)
    $latestCopilot = $reviewPages |
        ForEach-Object { $_.data.repository.pullRequest.reviews.nodes } |
        Where-Object {
            $null -ne $_.author -and $_.author.login -eq 'copilot-pull-request-reviewer' -and
            $null -ne $_.submittedAt -and $_.state -ne 'PENDING'
        } |
        Sort-Object submittedAt -Descending |
        Select-Object -First 1
    $copilot = $null
    if ($null -ne $latestCopilot) {
        $heading = [regex]::Match($latestCopilot.body,
            '(?m)^## Copilot review overview[ \t]*\r?\n(?:[ \t]*\r?\n)*###[ \t]+(?<recommendation>[^\r\n]+)')
        $commitSha = if ($null -ne $latestCopilot.commit) { $latestCopilot.commit.oid } else { $null }
        $copilot = [pscustomobject]@{
            State = $latestCopilot.state
            Recommendation = if ($heading.Success) { $heading.Groups['recommendation'].Value.Trim() } else { $null }
            Url = $latestCopilot.url
            SubmittedAt = $latestCopilot.submittedAt
            CommitSha = $commitSha
            IsCurrentHead = $null -ne $commitSha -and $commitSha -eq $pull.headRefOid
            Body = $latestCopilot.body
        }
    }

    $copilotSuggestionCount = 0
    foreach ($thread in $threads) {
        $copilotSuggestionCount += $thread.CopilotSuggestionCount
    }

    $commit = $pull.commits.nodes[0].commit
    $rollup = $commit.statusCheckRollup
    $checks = [pscustomobject]@{
        CommitSha = $commit.oid
        State = 'NONE'
        Total = 0
        Passed = 0
        Pending = 0
        Failed = 0
        Skipped = 0
        CountsByState = @()
    }
    if ($null -ne $rollup) {
        $checks.State = $rollup.state
        $checks.Total = $rollup.contexts.totalCount
        $checks.CountsByState = @(
            @($rollup.contexts.checkRunCountsByState) + @($rollup.contexts.statusContextCountsByState) |
                Where-Object count -gt 0
        )
        foreach ($count in $checks.CountsByState) {
            switch -Regex ($count.state) {
                '^SUCCESS$' { $checks.Passed += $count.count }
                '^(EXPECTED|PENDING|QUEUED|WAITING|IN_PROGRESS)$' { $checks.Pending += $count.count }
                '^(ACTION_REQUIRED|CANCELLED|ERROR|FAILURE|STALE|STARTUP_FAILURE|TIMED_OUT)$' { $checks.Failed += $count.count }
                '^(NEUTRAL|SKIPPED)$' { $checks.Skipped += $count.count }
            }
        }
    }

    return [pscustomobject]@{
        HeadSha = $pull.headRefOid
        Mergeable = $pull.mergeable
        Decision = $pull.reviewDecision
        UnresolvedThreadCount = $threads.Count
        CopilotUnresolvedThreadCount = @($threads | Where-Object IsCopilot).Count
        CopilotSuggestionCount = $copilotSuggestionCount
        UnresolvedThreads = $threads
        CopilotReview = $copilot
        Checks = $checks
    }
}

function Get-MergeabilityGroup {
    param([object] $Result)

    $blockingRank = 0
    $branchRank, $branch = switch ($Result.Status) {
        'Updated' { 0; 'Updated' }
        'UpToDate' { 0; 'Up to date' }
        'Eligible' { 1; 'Behind (rebase available)' }
        'RebaseConflict' { 2; 'Rebase conflicts'; $blockingRank = 1 }
        'MergeConflict' { 3; 'Merge conflicts'; $blockingRank = 1 }
        'Skipped' { 4; 'Skipped'; $blockingRank = 2 }
        'Failed' { 5; 'Update failed'; $blockingRank = 2 }
    }

    $reviewRank = 1
    $reviewLabel = 'Review needed'
    $checksRank = 4
    $checksLabel = 'Checks unavailable'
    $checksText = '? CI unavailable'
    if ($Result.ReviewStatus -eq 'Failed') {
        $blockingRank = 2
        $reviewRank = 5
        $reviewLabel = 'Review lookup failed'
    } else {
        $review = $Result.Review
        $copilot = $review.CopilotReview
        $checks = $review.Checks
        $checksRank, $checksLabel, $checksText = switch ($checks.State) {
            'SUCCESS' { 0; 'Checks passing'; "`u{2713} CI $($checks.Total)" }
            { $_ -in 'PENDING', 'EXPECTED' } { 1; 'Checks pending'; "`u{23F3} CI $($checks.Pending) pending" }
            'NONE' { 2; 'No checks'; '? CI none' }
            { $_ -in 'FAILURE', 'ERROR' } {
                3; 'Checks failed'
                $text = "`u{2717} CI $($checks.Failed) failed"
                if ($checks.Pending -gt 0) {
                    $text += ", $($checks.Pending) pending"
                }
                $text
            }
        }
        if ($Result.Status -ne 'Failed' -and $Result.HeadSha -ne $review.HeadSha) {
            $blockingRank = 2
            $branchRank = 4
            $branch = 'Branch changed; rerun needed'
        } elseif ($Result.Status -notin 'Failed', 'Skipped') {
            if ($review.Mergeable -eq 'CONFLICTING') {
                $blockingRank = 1
                $branchRank = 3
                $branch = 'Merge conflicts'
            } elseif ($review.Mergeable -eq 'UNKNOWN' -and $blockingRank -eq 0) {
                $blockingRank = 2
                $branch += '; mergeability unknown'
            }
        }

        if ($review.Decision -eq 'CHANGES_REQUESTED' -or
            ($null -ne $copilot -and $copilot.State -eq 'CHANGES_REQUESTED')) {
            $reviewRank = 4
            $reviewLabel = 'Changes requested'
        } elseif ($null -ne $copilot -and $copilot.State -ne 'DISMISSED' -and $copilot.Recommendation -match 'Changes recommended') {
            $reviewRank = 4
            $reviewLabel = 'Changes recommended'
        } elseif ($null -ne $copilot -and $copilot.State -ne 'DISMISSED' -and $copilot.Recommendation -match 'Needs a closer look') {
            $reviewRank = 3
            $reviewLabel = 'Needs a closer look'
        } elseif ($review.Decision -eq 'APPROVED') {
            $reviewRank = 0
            $reviewLabel = 'Approved'
        } elseif ($null -ne $copilot -and $copilot.IsCurrentHead -and $copilot.State -ne 'DISMISSED' -and
            ($copilot.State -eq 'APPROVED' -or $copilot.Recommendation -match 'Approval recommended')) {
            $reviewRank = 0
            $reviewLabel = 'Approval recommended'
        } elseif ($null -eq $copilot) {
            $reviewLabel = 'No Copilot review'
        } elseif (-not $copilot.IsCurrentHead) {
            $reviewLabel = 'Review needs refresh'
        } else {
            $reviewLabel = 'Review needed'
        }

        if ($review.UnresolvedThreadCount -gt 0 -and $reviewRank -lt 2) {
            $reviewRank = 2
            $reviewLabel += '; unresolved threads'
        }
    }

    $tier = if ($blockingRank -eq 2) {
        4
    } elseif ($blockingRank -eq 1) {
        3
    } elseif ($reviewRank -ge 2 -or $checksRank -eq 3) {
        2
    } elseif ($branchRank -gt 0 -or $reviewRank -gt 0 -or $checksRank -gt 0) {
        1
    } else {
        0
    }
    $branchIcon = switch ($branchRank) {
        0 { "`u{2713}" }
        1 { "`u{2193}" }
        { $_ -in 2, 3 } { "`u{26A0}" }
        default { '?' }
    }
    $reviewIcon = switch ($reviewRank) {
        0 { "`u{2713}" }
        1 { '?' }
        { $_ -in 2, 3 } { "`u{26A0}" }
        4 { "`u{2717}" }
        5 { '?' }
    }
    if ($blockingRank -eq 2) {
        $branchIcon = '?'
    }

    return [pscustomobject]@{
        Name = "$branch; $reviewLabel; $checksLabel"
        Tier = $tier
        ChecksRank = $checksRank
        ReviewRank = $reviewRank
        BranchRank = $branchRank
        Icons = "$branchIcon branch | $reviewIcon review | $checksText"
        Result = $Result
    }
}

function Write-ResultSummary {
    param([object[]] $Results)

    $unresolved = @($Results | Where-Object { $_.ReviewStatus -eq 'Available' -and $_.Review.UnresolvedThreadCount -gt 0 })
    Write-Host "`nMergeability: $($Results.Count) PRs, $($unresolved.Count) with unresolved threads"
    Write-Host "  `u{1F4AC} = unresolved threads; * = review of an earlier or unavailable commit."
    $ranked = @($Results | ForEach-Object { Get-MergeabilityGroup -Result $_ })
    $groups = $ranked | Group-Object Name | Sort-Object `
        @{ Expression = { $_.Group[0].Tier } }, `
        @{ Expression = { $_.Group[0].ChecksRank } }, `
        @{ Expression = { $_.Group[0].ReviewRank } }, `
        @{ Expression = { $_.Group[0].BranchRank } }, Name
    foreach ($group in $groups) {
        Write-Host "`n$($group.Name) ($($group.Count))"
        foreach ($rankedItem in ($group.Group | Sort-Object { $_.Result.Number })) {
            $item = $rankedItem.Result
            $icons = $rankedItem.Icons
            if ($item.ReviewStatus -eq 'Available') {
                if ($null -ne $item.Review.CopilotReview -and -not $item.Review.CopilotReview.IsCurrentHead) {
                    $icons += ' *'
                }
                if ($item.Review.UnresolvedThreadCount -gt 0) {
                    $icons += " | `u{1F4AC}$($item.Review.UnresolvedThreadCount)"
                }
            }
            Write-Host "  #$($item.Number) $($item.Title) | $icons | $($item.Url)"
        }
    }

    foreach ($item in ($Results | Sort-Object Number)) {
        Write-Verbose "#$($item.Number) - $($item.Title); $($item.Status): $($item.Message)"
        if ($item.ReviewStatus -eq 'Failed') {
            Write-Verbose $item.ReviewError
        } else {
            Write-Verbose "PR review decision: $($item.Review.Decision); Copilot code suggestions: $($item.Review.CopilotSuggestionCount)"
            $checks = $item.Review.Checks
            Write-Verbose "Checks: $($checks.State); $($checks.Passed) passed, $($checks.Pending) pending, $($checks.Failed) failed, $($checks.Skipped) skipped."
            if ($null -ne $item.Review.CopilotReview) {
                Write-Verbose "GitHub review state: $($item.Review.CopilotReview.State); reviewed commit: $($item.Review.CopilotReview.CommitSha)"
            }
            foreach ($thread in $item.Review.UnresolvedThreads) {
                $location = $thread.Path
                if ($null -ne $thread.Line) {
                    $location += ":$($thread.Line)"
                }
                $author = if ($null -ne $thread.Author) { $thread.Author } else { 'Deleted author' }
                $outdated = if ($thread.IsOutdated) { '; outdated diff' } else { '' }
                Write-Verbose "$author; $location; suggestions: $($thread.SuggestionCount)$outdated"
            }
        }
    }
}

if (-not (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
    Write-Host 'GitHub CLI (gh) is required.'
    exit 1
}

try {
    $helpText = Invoke-Gh -Arguments @('pr', 'update-branch', '--help')
    if (($helpText -join [Environment]::NewLine) -notmatch '--rebase') {
        throw [InvalidOperationException]::new('Install a GitHub CLI version which supports gh pr update-branch --rebase.')
    }

    $encodedBase = [Uri]::EscapeDataString($BaseBranch)
    $baseRef = Get-GitHubJson "repos/$Repository/git/ref/heads/$encodedBase"
    $baseSha = $baseRef.object.sha
    Write-Host "$Repository ($BaseBranch)"
    Write-Verbose "Using base snapshot $baseSha."
    $pullRequests = @(Invoke-Gh -Arguments @(
        'api', '--hostname', 'github.com', '--method', 'GET', '--paginate',
        "repos/$Repository/pulls?state=open&base=$encodedBase&per_page=100", '--jq', '.[] | {number, title}'
    ) | ForEach-Object { $_ | ConvertFrom-Json })
} catch [InvalidOperationException] {
    Write-Host 'Unable to initialize PR processing. Use -Verbose for diagnostics.'
    Write-Verbose $_.Exception.Message
    exit 1
}
$results = [Collections.Generic.List[object]]::new()

foreach ($pullRequest in $pullRequests) {
    $number = $pullRequest.number
    Write-Progress -Id 1 -Activity 'Updating PR branches and reading reviews' `
        -Status "#$number ($($results.Count + 1)/$($pullRequests.Count))" -PercentComplete (100 * $results.Count / $pullRequests.Count)
    $result = [pscustomobject][ordered]@{
        Number = [int] $number
        Url = "https://github.com/$Repository/pull/$number"
        Title = $pullRequest.title
        HeadRepository = $null
        HeadBranch = $null
        BaseSha = $baseSha
        PreviousHeadSha = $null
        HeadSha = $null
        BehindBy = $null
        Status = 'Skipped'
        Message = $null
        ReviewStatus = 'Pending'
        ReviewError = $null
        Review = $null
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
            } elseif ($WhatIfPreference) {
                $result.Status = 'Eligible'
                $result.Message = 'Preview: ready for rebase.'
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
                $result.Message = 'Update declined.'
            }
        }
    } catch [InvalidOperationException] {
        $result.Status = 'Failed'
        $result.Message = $_.Exception.Message
    }

    try {
        $result.Review = Get-ReviewReport -Number $result.Number
        $result.ReviewStatus = 'Available'
    } catch [InvalidOperationException] {
        $result.ReviewStatus = 'Failed'
        $result.ReviewError = $_.Exception.Message
    }

    $results.Add($result)
    if ($PassThru) {
        $result
    }
}

Write-Progress -Id 1 -Activity 'Updating PR branches and reading reviews' -Completed
Write-ResultSummary -Results $results

if (@($results | Where-Object { $_.Status -eq 'Failed' -or $_.ReviewStatus -eq 'Failed' }).Count -gt 0) {
    Write-Host "`nSome operations failed. Use -Verbose for diagnostics or -PassThru for full results."
    exit 1
}
