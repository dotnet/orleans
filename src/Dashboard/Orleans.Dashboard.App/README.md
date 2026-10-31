# Orleans Dashboard App

This directory contains the frontend web application for the Orleans Dashboard, built with React and Vite.

## Structure

```
Orleans.Dashboard.App/
├── src/              # React application source code
│   ├── components/   # Reusable React components
│   ├── grains/       # Grain-specific views
│   ├── logstream/    # Log streaming components
│   ├── overview/     # Dashboard overview components
│   ├── reminders/    # Reminders view components
│   ├── silos/        # Silo management components
│   ├── lib/          # Utility libraries
│   └── index.jsx     # Application entry point
├── public/           # Static assets (copied to build output)
│   ├── favicon.ico
│   ├── OrleansLogo.png
│   └── *.css, *.js   # Third-party CSS/JS libraries
├── screenshots/      # Dashboard screenshots for documentation
├── index.html        # HTML template
├── package.json      # npm dependencies and scripts
├── vite.config.ts    # Vite build configuration
└── tsconfig.json     # TypeScript configuration
```

## Development

### Prerequisites

- Node.js (v24 or later)
- npm

### Setup

```bash
cd src/Dashboard/Orleans.Dashboard.App
npm run restore -- --prefer-offline --no-audit
```

### Development Server

Run the Vite development server for hot module replacement:

```bash
npm run dev
```

This will start the development server at `http://localhost:5173` (or another port if 5173 is in use).

### Building

To build the production bundle:

```bash
npm run build
```

This builds the application to `dist/`. Building the Orleans.Dashboard C# project embeds every file in that directory as a resource, including fonts, images, and source maps.

### Navigation Tests

The browser tests exercise the production bundle's counter refresh, slow requests, route changes, narrow-screen navigation, keyboard dismissal, and desktop breakpoint:

```bash
npx playwright install chromium
npm run test:ui
```

### Preview Production Build

To preview the production build locally:

```bash
npm run preview
```

## Integration with Orleans.Dashboard

The frontend application is automatically built when you build the Orleans.Dashboard C# project. The build process is managed by `Orleans.Dashboard.Frontend.targets` which:

1. Runs `npm run restore` to install the locked dependency tree when dependency manifests, the installer script, npm configuration, or Node.js/npm versions change, or when the installed dependency stamp is missing.
2. Runs `npm run build` when source paths or content, build configuration, or dependencies change, or when a previously built asset is missing.
3. Embeds every built asset as a resource in the Orleans.Dashboard assembly.

Source edits reuse installed dependencies. Multi-framework builds share one frontend build. Clean and Rebuild refresh the compiled frontend while retaining the stamped `node_modules` dependency tree. Remove `node_modules` to request a fresh installation.

Every outer build refreshes dependency identity and configuration using three discovery commands: `node --version`, `npm --version`, and a scoped `npm config get`. These commands run once across all target frameworks, including on unchanged builds, keeping tool and configuration changes visible while retaining their subprocess startup and execution cost.

`npm run restore` uses the configured npm registry and cache. The .NET build supplies
bounded request retries, and the installer makes up to three clean-install attempts
when npm reports `ECONNRESET`, including resets while streaming a response body.
Each failed attempt prints npm's error and debug log; persistent failures fail the
build. Lifecycle scripts execute as part of the installation, and their failures
are reported immediately. The installer regression tests run with `npm run test:install`.

## Migration from Browserify

This project was migrated from Browserify to Vite for improved:
- Build performance
- Development experience with HMR
- Modern JavaScript/TypeScript support
- Better tree-shaking and optimization

The migration updated:
- React from v15 to v18
- Chart.js from v2 to v4
- Build tooling from Browserify/Babel to Vite
- Module system from CommonJS to ESM
