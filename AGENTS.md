# Repository workflow

- Open pull requests against `dotnet/orleans`.
- Do not push feature branches directly to `dotnet/orleans` or the `upstream` remote.
- Push feature branches to your personal fork, which is configured as `origin`.
- Create PRs using `dotnet/orleans` as the base repository and `origin`/personal fork branches as the head.

Example:

```powershell
git push --set-upstream origin <branch>
gh pr create --repo dotnet/orleans --base main --head ReubenBond:<branch>
```
