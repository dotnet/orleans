# Implementation principles

Apply these principles during design, implementation, and validation, alongside
correctness, security, compatibility, concurrency, and reliability.

## Layering, contracts, and invariants

- Establish each layer's required and provided guarantees. Keep responsibilities
  and invariant enforcement in their owning layer; preserve contracts across
  callers, overrides, concurrency, failures, and lifecycle transitions.
- Document non-obvious or changed contracts near the code in comments or XML API
  docs; use architecture documentation for broader design context.
- Use side-effect-free assertions at API boundaries for caller preconditions and
  internal invariants. Prefer debug-time assertions (`Debug.Assert`) for product
  programming errors where contracts are established or maintained. Exercise
  assertion-enabled code in CI and surface assertion failures as test failures.
  Preserve required behavior and external-input validation when assertions are compiled out.
- Within established contracts, rely on proven guarantees rather than adding
  redundant assertions or checks. Avoid unnecessary fallback state, retries, or catches.
  Validate external inputs and handle realistic failures with explicit error reporting.
- Seek additional invariants that simplify code. Establish their feasibility,
  owning layer, and enforcement cost, then enforce and document them before use.

## Clean, minimal implementations

- Make the smallest complete change; reuse patterns and helpers. Keep unrelated
  refactoring and formatting out of scope.
- Add abstractions, inheritance, virtual dispatch, options, fields, properties,
  caches, and states only for concrete requirements or boundaries. Derive values
  from existing state when correctness and efficiency permit.

## Runtime performance

- Assess CPU, throughput, latency, and scalability under relevant workloads,
  including algorithmic complexity, repeated work, I/O, contention, and scheduling.
- Prioritize low allocation and retained-memory costs. Minimize copies, object sizes
  (including alignment), and retained graphs, especially per grain, activation, or
  message. Account for count and lifetime; justify optimizations by net benefit,
  ownership safety, and complexity. Support quantitative claims with evidence.

## Testing and documentation

- Assert observable outcomes and contracts across relevant boundaries, failures,
  and transitions. Use Accordant model-based tests, CsCheck property-based tests,
  Verify for xUnit snapshots (`Verify.XunitV3`), or unit/integration tests as
  appropriate. Reuse test infrastructure; follow `test/AGENTS.md` under `test/`.
- Aim for greater than 80% CI code coverage. Consult the automated PR coverage
  comment for affected-code gaps and regressions; state when results are unavailable.
  Use behavioral assertions to establish guarantees.
- Match documentation to affected behavior, audience, and document type. Update
  READMEs only for highly relevant end-user setup, usage, or public behavior.
  Describe affirmative behavior and outcomes.
- For reviews, use `.github/skills/code-review/SKILL.md`.

# Repository workflow

- **New pull requests:** branch from `dotnet/orleans`'s `main`, push to the authenticated user's fork, and open the PR against `dotnet/orleans`.
- **Existing contributor pull requests:** when maintainer edits are enabled and authentication permits, push updates to the PR author's head fork and branch.
- **Every push:** run `git remote -v` and verify the destination by URL. Use the authenticated user's fork for new work or the PR author's fork for an existing PR; never rely on remote names or hard-code `origin`.
- Never push a feature branch to a remote whose URL points to `github.com/dotnet/orleans`, over HTTPS or SSH. Delete it immediately if this happens accidentally.
- After rebasing a PR branch, use `--force-with-lease`, never `--force`.
- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commits and PR titles. Update nonconforming PR titles during review.
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
