import type { APIRoute } from 'astro';
import { getPackages } from '../../../../../lib/api/collection';
import { renderMemberKindMarkdown, markdownResponse } from '../../../../../lib/api/markdown';
import { buildMemberKindRoutes } from '../../../../../lib/api/routes';
import type {
  ApiType,
  MemberKind,
  PackageApiDocument,
} from '../../../../../lib/api/types';

export const prerender = true;

export async function getStaticPaths() {
  const packages = (await getPackages()).map((entry) => entry.data);
  return buildMemberKindRoutes(packages).map((route) => ({
    params: {
      package: route.packageSlug,
      type: route.typeSlug,
      memberKind: route.memberKindSlug,
    },
    props: { pkg: route.pkg, type: route.type, kind: route.kind },
  }));
}

export const GET: APIRoute = ({ props }) =>
  markdownResponse(
    renderMemberKindMarkdown(
      props.pkg as PackageApiDocument,
      props.type as ApiType,
      props.kind as MemberKind,
      import.meta.env.BASE_URL,
    ),
  );
