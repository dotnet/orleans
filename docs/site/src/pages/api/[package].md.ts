import type { APIRoute } from 'astro';
import { getPackages } from '../../lib/api/collection';
import { renderPackageMarkdown, markdownResponse } from '../../lib/api/markdown';
import { buildPackageRoutes } from '../../lib/api/routes';
import type { PackageApiDocument } from '../../lib/api/types';

export const prerender = true;

export async function getStaticPaths() {
  const packages = (await getPackages()).map((entry) => entry.data);
  return buildPackageRoutes(packages).map((route) => ({
    params: { package: route.packageSlug },
    props: { pkg: route.pkg },
  }));
}

export const GET: APIRoute = ({ props }) =>
  markdownResponse(
    renderPackageMarkdown(props.pkg as PackageApiDocument, import.meta.env.BASE_URL),
  );
