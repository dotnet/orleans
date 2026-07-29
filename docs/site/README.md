# Orleans documentation site

This Astro + Starlight site is the maintained home for Orleans documentation.
The conceptual Markdown sources live under `src/content/docs`;
`npm run prepare:docs` emits ignored `.mdx` siblings under `/docs/` so Starlight
can render the imported Microsoft Learn syntax.

Use Node.js 24 or later:

```powershell
npm install
npm run dev
```

After a production build, serve the exact static artifact with `npm run preview`.

Run the focused conversion tests, Astro type checks, strict snippet expansion,
Starlight link validation, Pagefind indexing, and production build with:

```powershell
npm run validate
```

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

Regenerate committed API data after changing the public source surface:

```powershell
pwsh ../scripts/Generate-ApiData.ps1 -Parallelism 4
```

GitHub Actions validates generated API freshness, compiles documentation snippets
and samples, builds the complete site, and deploys `dist` to GitHub Pages from
`main`. Pull requests receive a downloadable site artifact but never receive
deployment permissions.

Before the first deployment, set **Settings → Pages → Source** to **GitHub
Actions**. The workflow publishes after pushes to `main`, nightly at 09:00 UTC,
and on manual dispatch.

The production build also emits compatibility redirects for every URL in the
legacy `gh-pages` sitemap. Existing pages retain their exact URL when the new
site owns it; retired documentation and blog URLs redirect to the nearest
current documentation entry point.

`npm run audit:output` scans the complete rendered site for duplicate or missing
page headings, leaked Microsoft Learn directives, malformed API signatures and
operator names, oversized navigation, missing legacy redirects, and GitHub
Pages size-limit regressions.
