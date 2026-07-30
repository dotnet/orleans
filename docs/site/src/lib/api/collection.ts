import { getCollection, type CollectionEntry } from 'astro:content';
import { assertUniqueApiRoutes } from './packages';
import type { PackageApiDocument } from './types';

export type PackageCollectionEntry = Omit<CollectionEntry<'packages'>, 'data'> & {
  data: PackageApiDocument;
};

let packagesPromise: Promise<PackageCollectionEntry[]> | undefined;

export async function getPackages(): Promise<PackageCollectionEntry[]> {
  if (!import.meta.env.PROD) {
    return loadPackages();
  }
  packagesPromise ??= loadPackages();
  return packagesPromise;
}

async function loadPackages(): Promise<PackageCollectionEntry[]> {
  const entries = await getCollection('packages');
  const packages = entries.map((entry) => ({
    ...entry,
    data: entry.data as PackageApiDocument,
  }));
  assertUniqueApiRoutes(packages.map((entry) => entry.data));
  return packages.sort((left, right) =>
    left.data.package.name.localeCompare(right.data.package.name),
  );
}
