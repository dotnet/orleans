# PackageJsonGenerator

Generates deterministic per-package JSON describing the public .NET API in an Orleans assembly. The schema matches the package API data consumed by the `microsoft/aspire.dev` Astro site.

Use `docs/scripts/Generate-ApiData.ps1` from the repository root to discover and build the packable Orleans projects, assemble each project's reference closure, and generate the complete package data set.

```powershell
pwsh ./docs/scripts/Generate-ApiData.ps1
```

The default output is `docs/site/src/data/pkgs`. Pass `-OutputDirectory` to generate into a staging directory, `-TargetFramework` to select another supported TFM, or `-SkipBuild` when all selected package projects have already been built.

Projects are discovered from `src/**/*.csproj` using evaluated MSBuild properties. A project is included when it targets the selected framework and has both `IsPackable=true` and `IncludeBuildOutput=true`. This excludes test projects, implementation-only projects, analyzers, code generators, and assembly-free meta-packages without maintaining a package list.

Each output file is named `{PackageId}.{PackageVersion}.json`. Only public types and members declared by that package assembly are emitted; project, package, and framework dependencies are resolution-only Roslyn references. Portable PDB paths are resolved against the verified local repository tree and a tracked-file allowlist from the advertised commit, avoiding network-dependent output and broken generated/inferred links. The script validates XML documentation, package metadata, source repository/commit information, and duplicate type identities before removing stale generator-owned JSON files.

By default, source links target the latest commit which changed `src`. Generation fails when `src` has uncommitted changes. Automation can pass `-SourceCommit` to select an explicit full commit SHA.

## Upstream attribution

This tool is adapted from [`microsoft/aspire.dev`'s `PackageJsonGenerator`](https://github.com/microsoft/aspire.dev/tree/9aa68083af47da79b63bc15b80f44c1927bf9c08/src/tools/PackageJsonGenerator) at commit `9aa68083af47da79b63bc15b80f44c1927bf9c08`.

The adapted source retains the upstream .NET Foundation and MIT license headers. Orleans-specific changes cover repository URL normalization, relevant Orleans attributes, project/package discovery, and local build orchestration.
