import {
  genericArity,
  memberSlug,
  memberKindOrder,
  memberKindSlugs,
  packageSlug,
  slugify,
} from './packages';
import type { ApiMember, ApiType, MemberKind, PackageApiDocument } from './types';

export interface ApiPackageRoute {
  packageSlug: string;
  pkg: PackageApiDocument;
}

export interface ApiTypeRoute extends ApiPackageRoute {
  typeSlug: string;
  type: ApiType;
}

export interface ApiMemberKindRoute extends ApiTypeRoute {
  memberKindSlug: string;
  kind: MemberKind;
}

export interface ApiMemberRoute extends ApiMemberKindRoute {
  memberSlug: string;
  member: ApiMember;
}

export function buildPackageRoutes(packages: PackageApiDocument[]): ApiPackageRoute[] {
  return packages.map((pkg) => ({
    packageSlug: packageSlug(pkg.package.name),
    pkg,
  }));
}

export function buildTypeRoutes(packages: PackageApiDocument[]): ApiTypeRoute[] {
  return buildPackageRoutes(packages).flatMap((packageRoute) =>
    packageRoute.pkg.types
      .filter((type) => type.name.length > 0)
      .map((type) => ({
        ...packageRoute,
        typeSlug: slugify(type.name, genericArity(type)),
        type,
      })),
  );
}

export function buildMemberKindRoutes(packages: PackageApiDocument[]): ApiMemberKindRoute[] {
  return buildTypeRoutes(packages).flatMap((typeRoute) => {
    const members = typeRoute.type.members ?? [];
    return memberKindOrder
      .filter((kind) => members.some((member) => member.kind === kind))
      .map((kind) => ({
        ...typeRoute,
        kind,
        memberKindSlug: memberKindSlugs[kind],
      }));
  });
}

export function buildMemberRoutes(packages: PackageApiDocument[]): ApiMemberRoute[] {
  return buildMemberKindRoutes(packages).flatMap((kindRoute) =>
    (kindRoute.type.members ?? [])
      .filter((member) => member.kind === kindRoute.kind)
      .map((member) => ({
        ...kindRoute,
        member,
        memberSlug: memberSlug(member),
      })),
  );
}
