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
with merge and rebase conflicts first. After processing branches, reads review
threads and submitted reviews using GitHub GraphQL. The Review property contains
unresolved threads (including outdated threads), code suggestion counts, and the
latest Copilot review's state, overview recommendation, URL, and reviewed commit.
Recommendations are extracted from Copilot's Markdown overview heading; the full
review body is included for context. Fails after reporting update or review errors.
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf
.EXAMPLE
.\update-pr-branches.ps1
.EXAMPLE
.\update-pr-branches.ps1 -WhatIf | ConvertTo-Json -Depth 12 | Set-Content pr-review-report.json
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
      reviewDecision
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

    return [pscustomobject]@{
        HeadSha = $pull.headRefOid
        Decision = $pull.reviewDecision
        UnresolvedThreadCount = $threads.Count
        CopilotUnresolvedThreadCount = @($threads | Where-Object IsCopilot).Count
        CopilotSuggestionCount = $copilotSuggestionCount
        UnresolvedThreads = $threads
        CopilotReview = $copilot
    }
}

function Write-ReviewSummary {
    param([object[]] $Results)

    $available = @($Results | Where-Object ReviewStatus -eq 'Available' | Sort-Object Number)
    $unresolved = @($available | Where-Object { $_.Review.UnresolvedThreadCount -gt 0 })
    Write-Host "`nPRs with unresolved review threads: $($unresolved.Count)"
    foreach ($item in $unresolved) {
        Write-Host "  $($item.Url) - $($item.Title)"
        Write-Host "    Threads: $($item.Review.UnresolvedThreadCount); Copilot threads: $($item.Review.CopilotUnresolvedThreadCount); Copilot code suggestions: $($item.Review.CopilotSuggestionCount)"
        foreach ($thread in $item.Review.UnresolvedThreads) {
            $location = $thread.Path
            if ($null -ne $thread.Line) {
                $location += ":$($thread.Line)"
            }
            $author = if ($null -ne $thread.Author) { $thread.Author } else { 'Deleted author' }
            $outdated = if ($thread.IsOutdated) { '; outdated diff' } else { '' }
            Write-Host "    $($thread.Url) - $author; $location; suggestions: $($thread.SuggestionCount)$outdated"
        }
    }

    $reviewed = @($available | Where-Object { $null -ne $_.Review.CopilotReview })
    Write-Host "`nLatest submitted Copilot reviews: $($reviewed.Count)"
    foreach ($item in $reviewed) {
        $review = $item.Review.CopilotReview
        $recommendation = if ($null -ne $review.Recommendation) { $review.Recommendation } else { 'Overview heading unavailable; see review' }
        $commitStatus = if ($review.IsCurrentHead) { 'current head' } else { 'earlier or unavailable commit' }
        Write-Host "  $($item.Url) - $($item.Title)"
        Write-Host "    $recommendation; GitHub review state: $($review.State); $commitStatus"
        Write-Host "    $($review.Url)"
    }

    Write-Host "`nPRs with no submitted Copilot review: $($available.Count - $reviewed.Count)"
    foreach ($item in ($available | Where-Object { $null -eq $_.Review.CopilotReview })) {
        Write-Host "  $($item.Url) - $($item.Title)"
    }

    $failed = @($Results | Where-Object ReviewStatus -eq 'Failed' | Sort-Object Number)
    Write-Host "`nReview reporting failures: $($failed.Count)"
    foreach ($item in $failed) {
        Write-Host "  $($item.Url) - $($item.ReviewError)"
    }
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
}

Write-Host "`nCollecting review feedback for $($results.Count) pull requests."
foreach ($result in $results) {
    try {
        $result.Review = Get-ReviewReport -Number $result.Number
        $result.ReviewStatus = 'Available'
    } catch [InvalidOperationException] {
        $result.ReviewStatus = 'Failed'
        $result.ReviewError = $_.Exception.Message
        Write-Warning "#$($result.Number): $($result.ReviewError)"
    }

    $result
}

Write-Host "Processed $($results.Count) pull requests in $Repository targeting $BaseBranch."
Write-ResultSummary -Results $results
Write-ReviewSummary -Results $results

if (@($results | Where-Object { $_.Status -eq 'Failed' -or $_.ReviewStatus -eq 'Failed' }).Count -gt 0) {
    throw 'One or more pull request updates or review queries failed. See the per-PR results above.'
}
