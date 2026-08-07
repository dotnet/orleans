import path from 'node:path';

export const deploymentBase = '/orleans';

export function compatibilityOutputPath(route, outputRoot) {
  const prefix = `${deploymentBase}/`;
  if (typeof route !== 'string' || !route.startsWith(prefix)) {
    throw new Error(`Compatibility path '${route}' is outside the deployment base.`);
  }

  let relativeRoute;
  try {
    relativeRoute = decodeURIComponent(route.slice(prefix.length));
  } catch {
    throw new Error(`Compatibility path '${route}' has invalid URL encoding.`);
  }

  if (relativeRoute.includes('\0') || relativeRoute.includes('\\')) {
    throw new Error(`Compatibility path '${route}' contains an unsafe path separator or character.`);
  }
  if (
    path.isAbsolute(relativeRoute) ||
    path.posix.isAbsolute(relativeRoute) ||
    path.win32.isAbsolute(relativeRoute)
  ) {
    throw new Error(`Compatibility path '${route}' contains an absolute path.`);
  }

  const isDirectoryRoute = relativeRoute.length === 0 || relativeRoute.endsWith('/');
  const segments = relativeRoute.split('/');
  const pathSegments = isDirectoryRoute ? segments.slice(0, -1) : segments;
  if (
    pathSegments.some(
      (segment) =>
        segment.length === 0 || segment === '.' || segment === '..' || segment.includes(':'),
    )
  ) {
    throw new Error(`Compatibility path '${route}' contains an unsafe path segment.`);
  }

  const resolvedRoot = path.resolve(outputRoot);
  const outputPath = path.resolve(
    resolvedRoot,
    ...pathSegments,
    ...(isDirectoryRoute ? ['index.html'] : []),
  );
  const relativeOutput = path.relative(resolvedRoot, outputPath);
  if (
    relativeOutput.length === 0 ||
    relativeOutput === '..' ||
    relativeOutput.startsWith(`..${path.sep}`) ||
    path.isAbsolute(relativeOutput)
  ) {
    throw new Error(`Compatibility path '${route}' resolves outside the output directory.`);
  }

  return outputPath;
}
