import type { APIRoute } from 'astro';
import { getPackages } from '../../../../../../lib/api/collection';
import {
  markdownResponse,
  renderMemberMarkdown,
} from '../../../../../../lib/api/markdown';
import { buildMemberRoutes } from '../../../../../../lib/api/routes';
import type {
  ApiMember,
  ApiType,
  PackageApiDocument,
} from '../../../../../../lib/api/types';

export const prerender = true;

export async function getStaticPaths() {
  const packages = (await getPackages()).map((entry) => entry.data);
  return buildMemberRoutes(packages).map((route) => ({
    params: {
      package: route.packageSlug,
      type: route.typeSlug,
      memberKind: route.memberKindSlug,
      member: route.memberSlug,
    },
    props: { pkg: route.pkg, type: route.type, member: route.member },
  }));
}

export const GET: APIRoute = ({ props }) =>
  markdownResponse(
    renderMemberMarkdown(
      props.pkg as PackageApiDocument,
      props.type as ApiType,
      props.member as ApiMember,
      import.meta.env.BASE_URL,
    ),
  );
