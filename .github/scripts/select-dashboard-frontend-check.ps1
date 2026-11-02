[CmdletBinding()]
param(
    [string] $Repository = (Get-Location).Path,
    [string] $EventName = $env:GITHUB_EVENT_NAME,
    [string] $EventPath = $env:GITHUB_EVENT_PATH,
    [string] $TestedSha = $env:GITHUB_SHA,
    [string] $GitHubOutput = $env:GITHUB_OUTPUT,
    [string] $JsonOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([string[]] $Arguments)

    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $Repository
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return @{
            ExitCode = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Test-AffectedPath {
    param([string] $Path)

    # These directories own generation, SDK integration, and CI/bootstrap policy.
    # Broad directory scopes also cover new configuration/helpers and renamed files.
    if ($Path -match '^(?i:\.github/|\.azure/|eng/|src/Dashboard/)') {
        return $Path -cnotmatch '^src/Dashboard/Orleans\.Dashboard\.App/(README\.md|screenshots/.*|tests/.*)$' -and
            $Path -cne 'src/Dashboard/Orleans.Dashboard/README.md' -and
            $Path -cne 'src/Dashboard/Orleans.Dashboard.Abstractions/README.md'
    }

    return $Path -match '^(?i:\.gitattributes|\.gitmodules|global\.json|NuGet\.Config|Orleans\.slnx|Directory\.[^/]*\.(props|targets)|src/Directory\.[^/]*\.(props|targets)|Build\.cmd|build\.ps1|common\.ps1)$'
}

function Select-Check {
    $result = [ordered]@{
        required = $true
        reason = "Full reproduction: event '$EventName' has no proven PR comparison."
        tested_sha = $TestedSha
        comparison_base = $null
        changed_paths = @()
        affected_paths = @()
    }
    if ($EventName -ne 'pull_request') { return [pscustomobject] $result }
    if ($TestedSha -notmatch '^[0-9a-fA-F]{40}$' -or -not $EventPath -or -not (Test-Path -LiteralPath $EventPath -PathType Leaf)) {
        $result.reason = 'Full reproduction: immutable tested SHA or PR event payload is missing.'
        return [pscustomobject] $result
    }

    # Invalid JSON fails selection and the Build job rather than granting a skip.
    $event = Get-Content -Raw -LiteralPath $EventPath | ConvertFrom-Json -AsHashtable
    $base = $event['pull_request']?['base']?['sha']
    $head = $event['pull_request']?['head']?['sha']
    if ($base -notmatch '^[0-9a-fA-F]{40}$' -or $head -notmatch '^[0-9a-fA-F]{40}$') {
        $result.reason = 'Full reproduction: PR base/head commit identities are missing or invalid.'
        return [pscustomobject] $result
    }

    $checkout = Invoke-Git @('rev-parse', '--verify', 'HEAD')
    $commit = Invoke-Git @('cat-file', '-p', $TestedSha)
    if ($checkout.ExitCode -ne 0 -or $checkout.Output.Trim() -ine $TestedSha -or $commit.ExitCode -ne 0) {
        $result.reason = 'Full reproduction: checkout does not prove the immutable tested commit.'
        return [pscustomobject] $result
    }

    $headers = ($commit.Output -split '\r?\n\r?\n', 2)[0]
    $parents = @([regex]::Matches($headers, '(?m)^parent ([0-9a-f]{40})\r?$') | ForEach-Object { $_.Groups[1].Value })
    if ($parents.Count -ne 2 -or $parents[0] -ine $base -or $parents[1] -ine $head) {
        $result.reason = 'Full reproduction: tested commit is not the event base/head synthetic PR merge.'
        return [pscustomobject] $result
    }

    # Compare the complete tested merge tree to its fixed base parent, not HEAD^..HEAD
    # on the PR branch. Depth two suffices; absent trees in a shallow checkout run in full.
    # Disabling rename detection exposes both paths, including deletions/case-only renames.
    $diff = Invoke-Git @('diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--name-only', '-z', $base, $TestedSha, '--')
    if ($diff.ExitCode -ne 0) {
        $result.reason = 'Full reproduction: complete tested/base tree comparison is unavailable (possibly shallow history).'
        Write-Warning "$($result.reason) Git: $($diff.Error.Trim())"
        return [pscustomobject] $result
    }
    if ($diff.Output.Length -gt 0 -and -not $diff.Output.EndsWith("`0")) {
        throw 'Git returned an incomplete NUL-delimited changed-path list.'
    }

    $result.comparison_base = $base
    $result.changed_paths = @($diff.Output.Split("`0", [StringSplitOptions]::RemoveEmptyEntries))
    $result.affected_paths = @($result.changed_paths | Where-Object { Test-AffectedPath $_ })
    $result.required = $result.affected_paths.Count -gt 0
    $result.reason = if ($result.required) {
        'Full reproduction: the complete tested PR delta changes frontend inputs, outputs, integration, or CI policy.'
    } else {
        'Reproduction skipped: the complete immutable tested PR delta leaves frontend generation and verification unchanged.'
    }
    return [pscustomobject] $result
}

$selection = Select-Check
Write-Host $selection.reason
if ($GitHubOutput) {
    "required=$($selection.required.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $GitHubOutput -Encoding utf8NoBOM
}
if ($JsonOutput) {
    $selection | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $JsonOutput -Encoding utf8NoBOM
}
$selection
