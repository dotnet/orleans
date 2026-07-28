# Orleans documentation site

This Astro + Starlight site imports the conceptual Orleans documentation from
`dotnet/docs`. The upstream DocFX sources are preserved under
`src/content/docs`; `npm run prepare:docs` emits ignored `.mdx` siblings in the
same tree so Starlight and its link validator retain canonical route paths.

Use Node.js 24 or later:

```powershell
npm install
npm run dev
```

Run the focused conversion tests, Astro type checks, strict snippet expansion,
Starlight link validation, Pagefind indexing, and production build with:

```powershell
npm run validate
```

The samples page reads `samples/gallery.json` from the repository root. When the
catalog is absent, development builds render an explanatory empty state.
