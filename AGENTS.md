# Repository workflow

- Open pull requests against `dotnet/orleans`.
- Unless otherwise specified, create new branches from `main` in the upstream `dotnet/orleans` repository. Branch from the remote-tracking ref of whichever remote points at `dotnet/orleans`, which is not necessarily named `upstream`.
- Never push feature branches directly to the upstream `dotnet/orleans` repository, regardless of remote name.
- For new work and new pull requests, push the feature branch to the authenticated user's own fork of `dotnet/orleans`.
- For an existing pull request from a contributor fork, push updates directly to the PR author's head fork and branch when GitHub reports that maintainer edits are enabled and authentication permits the push. This is the expected workflow for updating that PR.
- Remote names are not reliable. Do not assume `origin` is your fork or that `upstream` is the only remote pointing at `dotnet/orleans` — in some checkouts `origin` *is* `dotnet/orleans` and the fork is under a different name. A checkout may also carry remotes for other contributors' forks.
- Before every push, run `git remote -v`, resolve the destination remote by URL rather than name, and confirm that its URL matches the intended repository: the authenticated user's fork for new work, or the PR author's head fork for an existing PR update.
- Never push a feature branch to a remote whose URL points to `github.com/dotnet/orleans`, whether the URL uses HTTPS or SSH, and never hard-code `git push origin` without checking first.
- After rebasing an existing pull request branch, use `git push --force-with-lease`; never use an unconditional force push.
- If a branch does land on `dotnet/orleans` by mistake, delete it immediately with `git push <that-remote> --delete <branch>`, then re-push to the fork.
- Create PRs using `dotnet/orleans` as the base repository and personal fork branches as the head.
- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commit messages from now on.
- PR titles must also follow Conventional Commits naming conventions because squash merges use the PR title as the resulting commit message.
- When reviewing a PR, evaluate its title for Conventional Commits conformance and update it as appropriate.
- When reviewing changes, check whether corresponding documentation or sample updates are needed in the `/docs` and `/samples` directories.
- When creating PRs, keep the PR description simple: explain the problem it addresses and the solution it implements, including the rationale where helpful. Do not include a section describing what commands to run to test the PR.

Example for new work. Resolve both remotes by URL first, then use those
variables for every subsequent git command, so a checkout where `origin` is
`dotnet/orleans` can't send a branch to the wrong repository:

```powershell
# Inspect the remotes before doing anything else.
git remote -v

# Resolve remotes by URL, not by name. Derive your own login so the lookup
# doesn't match another contributor's fork remote.
$login = gh api user --jq .login
$fork = git remote -v | Select-String "github\.com[:/]$login/[^\s]+\s+\(push\)$" | ForEach-Object { ($_ -split '\s+')[0] } | Select-Object -First 1
$upstream = git remote -v | Select-String 'github\.com[:/]dotnet/orleans(\.git)?\s+\(fetch\)$' | ForEach-Object { ($_ -split '\s+')[0] } | Select-Object -First 1
if (-not $fork) { throw "No remote points at a fork owned by $login." }
if ($fork -eq $upstream) { throw 'Refusing to push: resolved fork remote points at dotnet/orleans.' }

git fetch $upstream main
git switch -c <branch> "$upstream/main"
git push --set-upstream $fork <branch>
gh pr create --repo dotnet/orleans --base main --head "${login}:<branch>"
```

For an existing contributor pull request, first confirm that maintainer edits
are enabled. Then resolve a remote whose URL matches the PR author's head
repository and push to the PR's head branch:

```powershell
gh pr view <pr-number> --repo dotnet/orleans --json maintainerCanModify,headRepository,headRefName
git remote -v
git push <verified-contributor-remote> HEAD:<pr-head-branch>

# If the PR branch was rebased:
git push --force-with-lease <verified-contributor-remote> HEAD:<pr-head-branch>
```

Recovery, if a branch reaches `dotnet/orleans` anyway:

```powershell
git push $upstream --delete <branch>
```
