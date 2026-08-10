# Repository workflow

- **New pull requests:** branch from `dotnet/orleans`'s `main`, push to the authenticated user's fork, and open the PR against `dotnet/orleans`.
- **Existing contributor pull requests:** when maintainer edits are enabled and authentication permits, push updates to the PR author's head fork and branch.
- **Every push:** run `git remote -v` and verify the destination by URL. Use the authenticated user's fork for new work or the PR author's fork for an existing PR; never rely on remote names or hard-code `origin`.
- Never push a feature branch to a remote whose URL points to `github.com/dotnet/orleans`, over HTTPS or SSH. Delete it immediately if this happens accidentally.
- After rebasing a PR branch, use `--force-with-lease`, never `--force`.
- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commits and PR titles. Update nonconforming PR titles during review.
- When reviewing changes, check whether corresponding documentation or sample updates are needed in the `/docs` and `/samples` directories.
- Keep PR descriptions focused on the problem, solution, and rationale; omit test-command sections.
