export function mergeLegacyRedirects(redirects, legacyJekyllPages) {
  const result = { ...redirects };
  for (const [source, target] of Object.entries(legacyJekyllPages)) {
    const existing = result[source];
    if (Object.hasOwn(result, source) && existing !== target) {
      throw new Error(
        `Legacy redirect '${source}' has conflicting targets '${existing}' and '${target}'.`,
      );
    }
    result[source] = target;
  }
  return result;
}
