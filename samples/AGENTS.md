# Orleans samples guidance

These rules apply recursively to everything under `samples/`. The repository-level guidance also applies.

## Orchestration

- Use [Aspire](https://aspire.dev) app hosts for new or reworked multi-process
  samples and external dependencies. Model dependencies as resources with local
  emulators or containers; use `aspire run` rather than manual setup scripts.
- Wire Orleans through `Aspire.Hosting.Orleans`: `builder.AddOrleans("default")`, then `WithClustering(...)`, `WithGrainStorage(...)`, and so on, and give service projects a `WithReference(orleans)`. See `JournaledTodoList` for the canonical layout and `JournalingAzureBlobJson` for a single-service one.
- Name the app host project `<Sample>.AppHost`, set `IsAspireHost`, and reference the `Aspire.AppHost.Sdk`. Add a `<Sample>.ServiceDefaults` project when the sample has more than one service, and call `AddServiceDefaults()` from each service.
- Use `WaitFor(...)` for resources a service needs at startup, and `WithExternalHttpEndpoints()` for endpoints the user opens in a browser.
- Self-contained samples such as single-process `UseLocalhostClustering()` consoles
  can run without an app host.
- Samples that exist to demonstrate a specific deployment target, such as those under `Deployment/`, keep the infrastructure assets that target requires.

## Projects

- Every sample project targets `net10.0` and is non-packable.
- Keep each sample copyable and independently buildable. Reference Orleans through
  `Microsoft.Orleans.*` NuGet packages; project references stay within the sample.
- `Build-Samples.ps1` packs `Orleans.slnx` with a unique prerelease version and builds
  `Samples.slnx` against those packages.
- Each sample owns a `Directory.Packages.props` with versions for every dependency.
  The root samples file declares none; NuGet reads only the nearest file. Shared
  samples such as `Streaming/Common` use their shared parent as the copy-out unit.
- `PackageReference` elements must not carry `Version` or `VersionOverride`. Keep a package pinned to the same version across all samples, keep Aspire hosting and component versions aligned, and pin vulnerable transitive dependencies explicitly since `CentralPackageTransitivePinningEnabled` is on.
- Follow `.editorconfig`; keep idiomatic teaching code and comments focused on the
  Orleans concept being demonstrated.

## Registration and documentation

- `samples/Samples.slnx` must contain exactly the projects under `samples/`, grouped in a folder per sample. Add every new project, including app hosts and service defaults.
- Add new samples to `gallery.json` following an existing entry and the schema in
  `Validate-Samples.ps1`. Tag Aspire-orchestrated samples with `aspire`.
- `samples/README.md` is generated. Run `pwsh ./samples/Update-Readme.ps1` after changing `gallery.json`; never hand-edit it.
- Give each sample a `README.md` explaining what it demonstrates and how to run it. Imported samples keep their existing front matter and their original source repository and license.

## Validation

- Run `pwsh ./samples/Validate-Samples.ps1` for any change under `samples/`. It checks the gallery manifest, README freshness, solution membership, package version declarations, sample self-containment, and invokes `Build-Samples.ps1` to build all samples against locally packed Orleans packages.
- For instruction-only edits, use `-NoBuild`.
- Verify a new or changed sample still builds standalone: copy its folder outside the repository and run `dotnet build`. A sample using an Orleans package which has not been published yet is exempt until that package ships; the in-tree solution still builds it from the local package source.
- Building a sample must not require cloud credentials. Only running a sample may.
- Check `git diff --check` before committing.
