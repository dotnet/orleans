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

- Node.js (v18 or later)
- npm

### Setup

```bash
cd src/Dashboard/Orleans.Dashboard.App
npm install
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

This builds the application to `../Orleans.Dashboard/wwwroot/` which is then embedded as resources in the Orleans.Dashboard C# project.

### Preview Production Build

To preview the production build locally:

```bash
npm run preview
```

## Integration with Orleans.Dashboard

The frontend application is automatically built when you build the Orleans.Dashboard C# project. The build process is managed by `Orleans.Dashboard.Frontend.targets` which:

1. Installs npm packages if `node_modules` doesn't exist
2. Runs `npm run build` to compile the frontend
3. Embeds the built assets as resources in the Orleans.Dashboard assembly

The build uses incremental compilation, so the frontend is only rebuilt when source files change.

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
