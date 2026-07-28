import {
  apiRoot,
  genericArity,
  groupTypesByNamespace,
  memberKindLabels,
  memberKindOrder,
  memberKindPath,
  packagePath,
  slugify,
  typeDisplayName,
  typePath,
} from './packages';
import type { ApiType, PackageApiDocument } from './types';

interface SidebarLink {
  label: string;
  link: string;
}

interface SidebarGroup {
  label: string;
  collapsed: boolean;
  items: SidebarItem[];
}

export type SidebarItem = SidebarLink | SidebarGroup;

export function buildApiSidebar(
  packages: PackageApiDocument[],
  currentPackageName?: string,
): SidebarItem[] {
  const root: SidebarLink = { label: 'API packages', link: `${apiRoot}/` };
  const sorted = [...packages].sort((left, right) =>
    left.package.name.localeCompare(right.package.name),
  );
  if (currentPackageName) {
    const current = sorted.find((pkg) => pkg.package.name === currentPackageName);
    return current ? [root, packageSidebar(current, false)] : [root];
  }
  return [root, ...sorted.map((pkg) => packageSidebar(pkg, true))];
}

function packageSidebar(pkg: PackageApiDocument, collapsed: boolean): SidebarGroup {
  const namespaces = groupTypesByNamespace(pkg.types.filter((type) => type.name));
  const typeItems =
    namespaces.size > 1
      ? [...namespaces.entries()].map(([namespace, types]) => ({
          label: namespace,
          collapsed: true,
          items: types.map((type) => typeSidebar(pkg.package.name, type)),
        }))
      : [...namespaces.values()].flat().map((type) => typeSidebar(pkg.package.name, type));
  return {
    label: pkg.package.name,
    collapsed,
    items: [
      { label: 'Overview', link: packagePath(pkg.package.name) },
      ...typeItems,
    ],
  };
}

function typeSidebar(packageName: string, type: ApiType): SidebarItem {
  const members = type.members ?? [];
  if (type.kind === 'interface' && members.length === 0 && !type.enumMembers?.length) {
    return {
      label: typeDisplayName(type),
      link: typePath(packageName, type),
    };
  }
  const memberItems = memberKindOrder
    .filter((kind) => members.some((member) => member.kind === kind))
    .map((kind) => ({
      label: memberKindLabels[kind],
      link: memberKindPath(packageName, type, kind),
    }));
  return {
    label: typeDisplayName(type),
    collapsed: true,
    items: [
      {
        label: 'Overview',
        link: `${packagePath(packageName)}${slugify(type.name, genericArity(type))}/`,
      },
      ...memberItems,
    ],
  };
}
