# Orleans Dashboard App

This directory contains the frontend web application for the Orleans Dashboard, built with React and Vite. The production build is committed under `dist/`, and building the Orleans.Dashboard C# project embeds every file in `dist/` as a resource. .NET builds do not require Node.js or npm.

## Structure

```
Orleans.Dashboard.App/
├── src/              # React application source code
├── public/           # Static assets copied to the build output
├── scripts/          # npm installation helper and its tests
├── dist/             # Committed production build, embedded by Orleans.Dashboard
├── screenshots/      # Dashboard screenshots for documentation
├── tests/            # Playwright navigation tests
├── index.html        # HTML template
├── package.json      # npm dependencies and scripts
├── vite.config.ts    # Vite build configuration
└── tsconfig.json     # TypeScript configuration
```

## Development

Use the Node.js version in `.node-version` with the npm version in `package.json`. From this directory:

```shell
npm run restore
npm run dev
```

`npm run restore` runs `npm ci` and restarts the clean installation, up to three attempts, when npm reports a connection reset while downloading a package. Other failures are reported immediately.

The development server provides hot module replacement at `http://localhost:5173`.

## Updating the committed build

After changing the frontend or its dependencies, rebuild and commit `dist/` with the source change:

```shell
npm run restore
npm run build
```

The build writes the bundle, source map, fonts, images, and `THIRD-PARTY-NOTICES.md`, which Vite generates from the licenses of the bundled dependencies. The NuGet package also includes the notices as a standalone file.

The `Dashboard` workflow runs when a pull request or push changes this directory. It runs the same commands and fails if the output differs from the committed `dist/`, and runs the navigation tests. Frontend sources use LF line endings on every platform, so builds on Windows, Linux, and macOS produce identical files.

## Lifecycle diagrams

The lifecycle view renders ordered HTML stage cards connected by CSS arrows.
Startup follows increasing stage order; shutdown reverses it. Observer names and
methods are rendered as text, and the diagram scrolls for large lifecycles and
long labels. Cards use the selected light or dark theme.

## Navigation tests

The browser tests exercise lifecycle diagrams, counter refresh, slow requests, route changes, narrow-screen navigation, keyboard dismissal, and desktop breakpoint:

```shell
npx playwright install chromium
npm run test:ui
```

`npm run preview` serves the production build locally.
