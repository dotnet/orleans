# Repository workflow

- **New pull requests:** branch from `dotnet/orleans`'s `main`, push to the authenticated user's fork, and open the PR against `dotnet/orleans`.
- **Existing contributor pull requests:** when maintainer edits are enabled and authentication permits, push updates to the PR author's head fork and branch.
- **Every push:** run `git remote -v` and verify the destination by URL. Use the authenticated user's fork for new work or the PR author's fork for an existing PR; never rely on remote names or hard-code `origin`.
- Never push a feature branch to a remote whose URL points to `github.com/dotnet/orleans`, over HTTPS or SSH. Delete it immediately if this happens accidentally.
- After rebasing a PR branch, use `--force-with-lease`, never `--force`.
- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commits and PR titles. Update nonconforming PR titles during review.
- When reviewing changes, check whether corresponding documentation or sample updates are needed in the `/docs` and `/samples` directories.
- Keep PR descriptions focused on the problem, solution, and rationale; omit test-command sections.

# Package compatibility

- Packable source projects validate their produced packages against the released baseline configured in `src/Directory.Build.props`. Treat `CP*` and `PKV*` failures as compatibility findings, not warnings to disable globally.
- When a public API break is intentional, first run the affected project's normal Release pack to capture the compatibility failure:
  `dotnet pack <project> --configuration Release`.
- Regenerate that project's suppression file with:
  `dotnet pack <project> --configuration Release /p:GenerateCompatibilitySuppressionFile=true`.
- Review the generated `CompatibilitySuppressions.xml` beside the project. Retain only entries which describe the approved break, keep the suppression scoped to the affected package and API, and commit it with the breaking change.
- Run the normal Release pack again without `GenerateCompatibilitySuppressionFile` and require it to pass. Do not resolve compatibility failures by disabling package validation or adding `CP*`/`PKV*` diagnostics to `NoWarn`.
- For an intentional target-framework removal, add a package-specific `PackageValidationBaselineFrameworkToIgnore` item instead of an API suppression.
- After a release containing the break becomes the configured baseline, regenerate or remove the suppression file so obsolete entries do not accumulate.

# Generated API surfaces

- Files under `src/api` are generated public API surfaces. Do not edit them manually.
- After changing public API in a packable source project, regenerate that project's API file using the same restore configuration and GenAPI target as CI:
  `dotnet restore <project> --configfile .github/NuGet.GenAPI.Config -p:GenerateOrleansApiSource=true`,
  then `dotnet build <project> --framework net8.0 --configuration Release --no-incremental --no-restore -p:GenerateOrleansApiSource=true /t:"Build;GenAPIGenerateReferenceAssemblySource"`.
- Review and commit the generated `src/api` change with the implementation. The `GenerateOrleansApiSource` property activates the GenAPI package, and the dedicated NuGet config maps that package to its public transport feed; ordinary restores use only the repository's CFS source.
