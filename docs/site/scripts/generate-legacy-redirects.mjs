import { access, mkdir, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import legacyPaths from '../src/data/legacy-pages.json' with { type: 'json' };

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distRoot = path.join(siteRoot, 'dist');
const deploymentBase = '/orleans';

async function walk(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(entryPath)));
    } else if (entry.isFile()) {
      files.push(entryPath);
    }
  }
  return files;
}

function routeFromIndex(file) {
  const relative = path.relative(distRoot, file).replaceAll('\\', '/');
  const route = relative === 'index.html' ? '' : relative.replace(/\/index\.html$/, '');
  return `${deploymentBase}/${route}${route ? '/' : ''}`.replace(/\/{2,}/g, '/');
}

function canonicalizeLegacyRoute(legacyPath) {
  const relative = decodeURIComponent(legacyPath.slice(`${deploymentBase}/`.length))
    .replace(/\.html$/i, '')
    .replaceAll('_', '-')
    .toLowerCase();
  const route = relative.endsWith('/index') ? relative.slice(0, -'/index'.length) : relative;
  return `${deploymentBase}/${route}${route ? '/' : ''}`.replace(/\/{2,}/g, '/');
}

function routeKey(route) {
  return route
    .toLowerCase()
    .replace(/\/index\/?$/, '/')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

function targetFor(legacyPath, currentRoutes, conceptualRoutes) {
  if (legacyPath === `${deploymentBase}/index.html`) {
    return `${deploymentBase}/`;
  }
  if (legacyPath.startsWith(`${deploymentBase}/blog/`)) {
    return `${deploymentBase}/`;
  }

  const canonical = canonicalizeLegacyRoute(legacyPath);
  if (currentRoutes.has(canonical)) {
    return canonical;
  }

  const legacyBaseName = routeKey(canonical).split('-').at(-1);
  const matches = conceptualRoutes.filter((route) => routeKey(route).split('-').at(-1) === legacyBaseName);
  return matches.length === 1 ? matches[0] : `${deploymentBase}/docs/`;
}

const allFiles = await walk(distRoot);
const currentRoutes = new Set(
  allFiles.filter((file) => file.endsWith(`${path.sep}index.html`) || file === path.join(distRoot, 'index.html')).map(routeFromIndex),
);
const conceptualRoutes = [...currentRoutes].filter(
  (route) => route.startsWith(`${deploymentBase}/docs/`) && !route.startsWith(`${deploymentBase}/docs/api/`),
);

let written = 0;
let preserved = 0;
for (const legacyPath of legacyPaths) {
  if (!legacyPath.startsWith(`${deploymentBase}/`) || legacyPath.includes('..')) {
    throw new Error(`Unsafe legacy Pages path '${legacyPath}'.`);
  }

  const relative = decodeURIComponent(legacyPath.slice(`${deploymentBase}/`.length));
  const outputPath = path.join(distRoot, relative);
  try {
    await access(outputPath);
    preserved += 1;
    continue;
  } catch {
    // Generate a compatibility page when the new site does not already own this exact path.
  }

  const target = targetFor(legacyPath, currentRoutes, conceptualRoutes);
  const document = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta http-equiv="refresh" content="0; url=${target}">
    <link rel="canonical" href="https://dotnet.github.io${target}">
    <title>Orleans documentation moved</title>
    <script>location.replace(${JSON.stringify(target + '#legacy-redirect')}.replace('#legacy-redirect', location.hash));</script>
  </head>
  <body>
    <p>This page moved to <a href="${target}">${target}</a>.</p>
  </body>
</html>
`;
  await mkdir(path.dirname(outputPath), { recursive: true });
  await writeFile(outputPath, document, 'utf8');
  written += 1;
}

console.log(`Prepared ${written} legacy Pages redirects; ${preserved} paths are served by current pages.`);
