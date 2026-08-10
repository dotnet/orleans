# Orleans samples guidance

These rules apply recursively to everything under `samples/`. The repository-level guidance also applies.

## Orchestration

- Prefer [Aspire](https://aspire.dev) for orchestrating a sample's dependencies. New or reworked multi-process samples, and any sample needing an external dependency such as storage, a database, or a cache, should ship an app host.
- Model dependencies as Aspire resources rather than as manual setup steps, `docker-compose` files, or `run.cmd`/`run.sh` scripts. Use emulators or containers for local runs (`AddAzureStorage(...).RunAsEmulator()`, `AddRedis(...)`) so `aspire run` is the only prerequisite beyond the .NET SDK.
- Wire Orleans through `Aspire.Hosting.Orleans`: `builder.AddOrleans("default")`, then `WithClustering(...)`, `WithGrainStorage(...)`, and so on, and give service projects a `WithReference(orleans)`. See `JournaledTodoList` for the canonical layout and `JournalingAzureBlobJson` for a single-service one.
- Name the app host project `<Sample>.AppHost`, set `IsAspireHost`, and reference the `Aspire.AppHost.Sdk`. Add a `<Sample>.ServiceDefaults` project when the sample has more than one service, and call `AddServiceDefaults()` from each service.
- Use `WaitFor(...)` for resources a service needs at startup, and `WithExternalHttpEndpoints()` for endpoints the user opens in a browser.
- Leave a sample without an app host only when it is genuinely self-contained, such as a single-process console sample using `UseLocalhostClustering()`. Don't add an app host purely to wrap a localhost sample.
- Samples that exist to demonstrate a specific deployment target, such as those under `Deployment/`, keep the infrastructure assets that target requires.

## Projects

- Every sample project targets `net10.0` and is non-packable.
- Reference Orleans from the repository sources with `$(SourceRoot)src\...` project references, never from `Microsoft.Orleans.*` packages. `samples/Directory.Build.props` adds `Orleans.Core` automatically, plus the runtime and memory persistence when `Orleans.Server` is referenced.
- Declare every package version centrally in `samples/Directory.Packages.props` or the repository `Directory.Packages.props`. `PackageReference` elements in sample projects must not carry `Version` or `VersionOverride`, and validation enforces this.
- Keep Aspire hosting and component package versions aligned with each other.
- Follow the repository `.editorconfig`. Samples are teaching material, so favor clear, idiomatic code and comments that explain the Orleans concept being shown.

## Registration and documentation

- `samples/Samples.slnx` must contain exactly the projects under `samples/`, grouped in a folder per sample. Add every new project, including app hosts and service defaults.
- Add a `gallery.json` entry for each new sample with all of `slug`, `title`, `description`, `path`, `sourceRepository`, `image`, `languages`, `tags`, and `featured`, in that order. `path` and `image` must resolve inside `samples/`; use `null` when there is no image. Tag Aspire-orchestrated samples with `aspire`.
- `samples/README.md` is generated. Run `pwsh ./samples/Update-Readme.ps1` after changing `gallery.json`; never hand-edit it.
- Give each sample a `README.md` explaining what it demonstrates and how to run it. Imported samples keep their existing front matter and their original source repository and license.

## Validation

- Run `pwsh ./samples/Validate-Samples.ps1` for any change under `samples/`. It checks the gallery manifest, README freshness, solution membership, central package versions, and builds the full solution.
- Building a sample must not require cloud credentials. Only running a sample may.
- Check `git diff --check` before committing.
