# Repository workflow

- Open pull requests against `dotnet/orleans`.
- Unless otherwise specified, create new branches from `main` in the upstream `dotnet/orleans` repository (`upstream/main`).
- Do not push feature branches directly to `dotnet/orleans` or the `upstream` remote.
- Push feature branches to your personal fork, which is configured as `origin`.
- Create PRs using `dotnet/orleans` as the base repository and `origin`/personal fork branches as the head.

Example:

```powershell
git fetch upstream main
git switch -c <branch> upstream/main
git push --set-upstream origin <branch>
gh pr create --repo dotnet/orleans --base main --head ReubenBond:<branch>
```
