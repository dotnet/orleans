# Build the Orleans API reference

The API reference uses the repository-local DocFX tool and is generated from the public managed assemblies produced by Orleans source projects.

From the repository root, run:

```powershell
pwsh ./docs/scripts/Build-ApiDocs.ps1 -OutputDirectory ./Artifacts/docs/api
```

The script restores the local tool manifest, builds `Orleans.slnx` in Release configuration, builds any selected API project which is not part of the solution, generates DocFX metadata, and writes the static site to the requested output directory. Pass `-SkipBuild` when all selected projects have already been built in the requested configuration:

```powershell
pwsh ./docs/scripts/Build-ApiDocs.ps1 -SkipBuild -OutputDirectory ./docs/site/dist/api
```

Relative output paths are resolved from the repository root. The output directory must be empty or contain the marker from an earlier run; this prevents accidental deletion of unrelated files. It is replaced on each run and contains `index.html`, the generated `reference` tree, search data, and static assets. Its contents can be copied directly into Astro's `dist/api` directory. DocFX emits relative internal links, so the same output works from a local static server and when mounted at `/orleans/api/`.

## API scope

Project discovery is deterministic and does not use a hand-maintained package list. The script evaluates every `src/**/*.csproj` and includes projects which:

- target the requested framework (`net10.0` by default);
- have `IsPackable=true`; and
- have `IncludeBuildOutput=true`.

This excludes test projects outside `src`, non-packable implementation projects, analyzers, code generators, and managed-assembly-free meta-packages. Published testing libraries such as `Orleans.TestingHost` and the test-kit packages remain included because they ship public managed APIs.

The selected projects are written once each to a generated `.slnx` metadata manifest. DocFX's MSBuild workspace resolves each project's own dependency graph for compilation, but only projects explicitly listed in that manifest are emitted. This avoids both flattened dependency-version conflicts and duplicate metadata from transitive project references. The build fails on missing assemblies or XML documentation files and on duplicate generated API UIDs.
