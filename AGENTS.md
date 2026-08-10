# Repository workflow

- **New pull requests:** branch from `dotnet/orleans`'s `main`, push to the authenticated user's fork, and open the PR against `dotnet/orleans`.
- **Existing contributor pull requests:** when maintainer edits are enabled and authentication permits, push updates to the PR author's head fork and branch.
- **Every push:** run `git remote -v` and verify the destination by URL. Use the authenticated user's fork for new work or the PR author's fork for an existing PR; never rely on remote names or hard-code `origin`.
- Never push a feature branch to a remote whose URL points to `github.com/dotnet/orleans`, over HTTPS or SSH.
- After rebasing a PR branch, use `--force-with-lease`, never `--force`.
- If a branch does land on `dotnet/orleans` by mistake, delete it immediately with `git push <that-remote> --delete <branch>`, then re-push to the fork.
- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commits and PR titles. Update nonconforming PR titles during review.
- When reviewing changes, check whether corresponding documentation or sample updates are needed in the `/docs` and `/samples` directories.
- Keep PR descriptions focused on the problem, solution, and rationale; omit test-command sections.

For new work:

```powershell
git remote -v
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

For an existing contributor PR:

```powershell
gh pr view <pr-number> --repo dotnet/orleans --json maintainerCanModify,headRepository,headRefName
git remote -v
git push <verified-contributor-remote> HEAD:<pr-head-branch>
# After rebasing, use this instead:
git push --force-with-lease <verified-contributor-remote> HEAD:<pr-head-branch>
```
