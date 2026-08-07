# Orleans documentation site

This Astro + Starlight site is the maintained home for Orleans documentation.
The conceptual Markdown sources live under `src/content/docs`;
`npm run prepare:docs` emits ignored `.mdx` siblings under `/docs/` so Starlight
can render the imported Microsoft Learn syntax.

Use Node.js 24 or later:

```powershell
npm install
npm run api:generate
npm run dev
```

API generation builds the public Orleans packages and can take several minutes.
The generated package JSON is ignored by Git and should not be committed. You can
skip this step when API reference pages aren't needed for the local preview.

After a production build, serve the exact static artifact with `npm run preview`.

Run the focused conversion tests, Astro type checks, strict snippet expansion,
source-quality and aggregate project policy audits, Starlight link validation,
Pagefind indexing, and production build with:

```powershell
npm run validate
```

The source audit reports a rule ID, file, line, and remediation. It enforces:

- Orleans 10 guidance outside migration pages and explicitly versioned compatibility zones.
- Exactly one `toc.yml` entry per maintained conceptual page, with `includes` and
  snippet support files excluded; Architecture and internals and Event Sourcing
  remain first-class navigation sections.
- Package and stream-provider inventories against source project metadata,
  activity sources and lifecycle stages against source/API constants, documented
  metric names against `InstrumentNames.cs`, and sample paths against
  `samples/gallery.json`.
- Content hashes and reasons for legacy inline C# fences in
  `src/data/csharp-fence-exclusions.json`. Prefer a compiled `:::code` source for
  new examples; after reviewing a justified opt-out, refresh the hashes with
  `node scripts/audit-sources.mjs --update-csharp-fences`, then replace each new
  `REQUIRED` placeholder with a specific rationale.
- Source-aware and rendered link validation. Migrated Microsoft Learn-relative
  links must use canonical `https://learn.microsoft.com/...` URLs; rendered
  routes, encoded paths, redirects, and anchors must resolve under `/orleans/`.
  Source diagnostics include file/line provenance.
- Deduplicated external HTTP(S) validation with bounded concurrency, redirects,
  timeouts, retries, and HEAD-to-GET fallback. Definitive broken targets fail.
  Rate limits and transient network failures are reported explicitly without
  making CI nondeterministic. Pull-request content is untrusted: the probe rejects
  credentials, non-default ports, and any hostname/IP which can reach private,
  loopback, link-local, metadata, or other non-public networks. DNS answers are
  validated and pinned into the actual TLS connection at every redirect hop, so
  contributors cannot use redirects, rebinding, or proxy environment variables
  to turn link validation into an internal-network request.

`npm run build` and `npm run validate` discover every project under `docs/` and
`samples/`, require exact one-to-one membership in `docs/Docs.slnx`, evaluate an
exact `net10.0` target, and require every Orleans package reference to resolve to
`10.2.2`. Historical package versions are accepted only in a migration project
with a used, meaningful `OrleansDocumentationVersionException` property. To
compile the complete aggregate locally, run:

```powershell
dotnet build ../Docs.slnx --framework net10.0
```

To compile only the 38 checked-in documentation snippet projects sequentially,
run:

```powershell
pwsh src/content/docs/validate-snippets.ps1
```

Fix `DOCS001` by keeping current-release documentation versionless, moving
version-specific guidance into `migration/` or upgrade pages, or linking to those
pages. Fix `DOCS002`/`DOCS003` in `toc.yml`. For `DOCS004`, move the fence to a
compiled snippet or document the exception and update its hash. For `DOCS005`,
synchronize the authored reference with the named source inventory.
Every packable Orleans source package must be documented or have a reasoned entry
in `src/data/package-inventory-exclusions.json`.
Fix `PROJECT001`-`PROJECT003` by adding or removing projects with `dotnet sln`
until filesystem discovery and `docs/Docs.slnx` match exactly. Fix `PROJECT004`
by targeting only `net10.0`, and fix `PROJECT005` by updating the effective
Orleans package version to `10.2.2`. `PROJECT006` identifies an invalid, vague,
or stale migration exception. Fix `SNIPPET001`/`SNIPPET002` in the
reported snippet project before rebuilding.
Fix `LINK001` using the canonical URL in its remediation. Rendered-link failures
name the source file/line when the authored link can be mapped, otherwise the
rendered route. `src/data/external-link-allowlist.json` accepts exact URLs only;
each entry needs a narrow reason and stale entries fail validation.

Options remain an intentionally curated shortlist rather than a generated
catalog; the audit requires that page to identify the generated API reference as
the exhaustive source. Metric prose is similarly selective, but every metric
identifier it names must exist in runtime source.

The samples page reads `samples/gallery.json` from the repository root. When the
catalog is absent, development builds render an explanatory empty state.

## Native API reference

The `/docs/api/csharp/` route tree uses the same native rendering architecture as
[`microsoft/aspire.dev`](https://github.com/microsoft/aspire.dev): an Astro
`packages` content collection reads `src/data/pkgs/*.json`, and Starlight pages
render package, type, member-kind, and individual member routes with matching
`.md` companions.
The expected JSON shape is the output of Aspire's Roslyn/XML/PDB
`PackageJsonGenerator`, adapted by the Orleans API generator workstream.

Generate API data before previewing API pages or running a complete local build:

```powershell
npm run api:generate
```

GitHub Actions generates API data on demand, builds `docs/Docs.slnx` with a
diagnostic binlog, builds the complete site, and deploys `dist` to GitHub Pages
from `main`.
Pull requests receive a downloadable site artifact but never receive deployment
permissions.

Before the first deployment, set **Settings → Pages → Source** to **GitHub
Actions**. The workflow publishes after pushes to `main`, nightly at 09:00 UTC,
and on manual dispatch.

The production build also emits compatibility redirects for every URL in the
legacy `gh-pages` sitemap. Existing pages retain their exact URL when the new
site owns it; retired documentation and blog URLs redirect to the nearest
current documentation entry point. Explicit replacements in
`src/data/redirects.json` preserve inbound anchors and override the automatic
legacy-path matching.

`npm run audit:links` validates source-authored external links and every rendered
internal route and anchor. It runs automatically from both `npm run build` and
`npm run validate`.

`npm run audit:output` scans the complete rendered site for duplicate or missing
page headings, leaked Microsoft Learn directives, malformed API signatures and
operator names, oversized navigation, missing legacy redirects, and GitHub
Pages size-limit regressions.
