# Orleans Dashboard App

The Orleans Dashboard frontend uses React and Vite. Its deployable assets are committed under `dist/`. Building or packing `Orleans.Dashboard` validates their source and output content hashes and embeds every asset in the assembly using the .NET SDK.

## Development

Use the exact Node.js version in `.node-version` and npm version in `package.json`. From this directory:

```shell
npm ci
npm run dev
```

The development server provides hot module replacement at `http://localhost:5173`. `npm run build` produces a local production preview in `dist/`, and `npm run preview` serves it. Regenerate canonical assets before building the .NET project after frontend changes.

## Regenerating committed assets

```shell
npm run assets:generate
```

The generator checks the pinned Node/npm versions, snapshots the inputs into a temporary directory, installs the locked dependencies with `npm ci`, and builds in production mode. It uses an explicit environment, isolated npm configuration, and a fresh dependency tree. Registry fetches use bounded retries and the local npm download cache.

Commit `dist/`, `assets.inputs.sha256`, and `assets.outputs.sha256` together with the source change. Generation preserves source maps and adds `THIRD-PARTY-NOTICES.txt` containing the installed production dependencies' license notices. The .NET package also includes the notices as a standalone file.

Inputs comprise every file in `src/` and `public/`, plus every frontend-root file except this README, `.gitignore`, and the two generated manifests. This includes the HTML entry point, dependency manifests, lockfile, build configuration, generation and verification scripts, and toolchain pins. Keep build helpers within these boundaries so canonical generation can copy their complete inputs. Documentation screenshots and browser tests live outside the generation inputs.

Source text with JavaScript, TypeScript, C#, CSS, JSON, HTML, SVG, or TXT extensions, `.npmrc`, `.node-version`, and `.env*` files is hashed and copied with LF line endings. Binary source assets retain their bytes. Generated files are hashed byte-for-byte; Git attributes preserve their exact output bytes. Paths use case-sensitive relative names with `/` separators. File contents and the complete path sets determine freshness, including additions, removals, and renames.

Canonical generation uses production mode, UTC, and the `C` locale. Move `.env.local` and `.env.*.local` overrides out of this directory for regeneration. Add shared environment files to source control when the build uses them.

## Reproducibility

```shell
npm run assets:check
```

This independently installs and regenerates in a fresh temporary directory, then compares every generated file and both manifests with the committed files. The Windows and Linux CI build jobs require this step to succeed before the .NET build. An actionable .NET build error identifies stale source or output paths and directs contributors to explicit regeneration.

Framework discovery and Clean operate on the .NET project graph and intermediate outputs. The committed frontend files remain available across target frameworks and clean builds.

## Navigation tests

The Playwright tests exercise the production bundle's counter refresh, slow requests, route changes, narrow-screen navigation, keyboard dismissal, and desktop breakpoint:

```shell
npx playwright install chromium
npm run test:ui
```
