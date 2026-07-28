import type { APIRoute } from 'astro';
import { getPackages } from '../../../../../lib/api/collection';
import { renderTypeMarkdown, markdownResponse } from '../../../../../lib/api/markdown';
import { buildTypeRoutes } from '../../../../../lib/api/routes';
import type { ApiType, PackageApiDocument } from '../../../../../lib/api/types';

export const prerender = true;

export async function getStaticPaths() {
  const packages = (await getPackages()).map((entry) => entry.data);
  return buildTypeRoutes(packages).map((route) => ({
    params: { package: route.packageSlug, type: route.typeSlug },
    props: { pkg: route.pkg, type: route.type },
  }));
}

export const GET: APIRoute = ({ props }) =>
  markdownResponse(
    renderTypeMarkdown(
      props.pkg as PackageApiDocument,
      props.type as ApiType,
      import.meta.env.BASE_URL,
    ),
  );
