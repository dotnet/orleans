export function getBuildConcurrency(
  platform = process.platform,
  configured = process.env.ORLEANS_DOCS_BUILD_CONCURRENCY,
) {
  if (configured !== undefined && configured !== '') {
    const requestedConcurrency = Number(configured);
    if (!Number.isInteger(requestedConcurrency) || requestedConcurrency <= 0) {
      throw new Error('ORLEANS_DOCS_BUILD_CONCURRENCY must be a positive integer.');
    }

    return requestedConcurrency;
  }

  return platform === 'win32' ? 1 : 4;
}
