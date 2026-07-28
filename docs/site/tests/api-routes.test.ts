import { describe, expect, test } from 'vitest';
import fixture from './fixtures/package-api.json';
import {
  assertUniqueApiRoutes,
  findTypeByDocId,
  memberKindPath,
  typePath,
} from '../src/lib/api/packages';
import {
  buildMemberRoutes,
  buildMemberKindRoutes,
  buildPackageRoutes,
  buildTypeRoutes,
} from '../src/lib/api/routes';
import { buildApiSidebar } from '../src/lib/api/sidebar';
import type { PackageApiDocument } from '../src/lib/api/types';

const pkg = fixture as PackageApiDocument;

describe('native API routes', () => {
  test('builds package, generic type, and existing member-kind paths', () => {
    expect(buildPackageRoutes([pkg]).map((route) => route.packageSlug)).toEqual([
      'microsoft.orleans.core',
    ]);
    expect(buildTypeRoutes([pkg]).map((route) => route.typeSlug)).toEqual([
      'grain-1',
      'grainstatus',
    ]);
    expect(
      buildMemberKindRoutes([pkg]).map((route) => route.memberKindSlug),
    ).toEqual(['properties', 'methods']);
    expect(typePath(pkg.package.name, pkg.types[0])).toBe(
      '/docs/api/csharp/microsoft.orleans.core/grain-1/',
    );
    expect(memberKindPath(pkg.package.name, pkg.types[0], 'method')).toBe(
      '/docs/api/csharp/microsoft.orleans.core/grain-1/methods/',
    );
    expect(buildMemberRoutes([pkg]).map((route) => route.memberSlug)).toEqual([
      'identitystring',
      'getprimarykey-string',
    ]);
    expect(buildMemberRoutes([pkg])[1]).toMatchObject({
      packageSlug: 'microsoft.orleans.core',
      typeSlug: 'grain-1',
      memberKindSlug: 'methods',
      memberSlug: 'getprimarykey-string',
    });
    expect(
      buildMemberRoutes([pkg])[1].member.name,
    ).toBe('GetPrimaryKey');
    expect(
      `${memberKindPath(pkg.package.name, pkg.types[0], 'method')}getprimarykey-string/`,
    ).toBe(
      '/docs/api/csharp/microsoft.orleans.core/grain-1/methods/getprimarykey-string/',
    );
  });

  test('builds a package-aware Starlight sidebar', () => {
    const sidebar = buildApiSidebar([pkg], pkg.package.name);
    expect(sidebar).toHaveLength(2);
    expect(sidebar[0]).toEqual({ label: 'API packages', link: '/docs/api/csharp/' });
    expect(JSON.stringify(sidebar)).toContain(
      '/docs/api/csharp/microsoft.orleans.core/grain-1/methods/',
    );
  });

  test('fails deterministic generation on route collisions', () => {
    expect(() => assertUniqueApiRoutes([pkg, pkg])).toThrow(
      "Duplicate API package route 'microsoft.orleans.core'",
    );
    expect(() =>
      assertUniqueApiRoutes([
        {
          ...pkg,
          types: [pkg.types[0], { ...pkg.types[0] }],
        },
      ]),
    ).toThrow("Duplicate API type route 'microsoft.orleans.core/grain-1'");

    expect(() =>
      assertUniqueApiRoutes([
        {
          ...pkg,
          types: [
            {
              ...pkg.types[0],
              members: [
                pkg.types[0].members![0],
                { ...pkg.types[0].members![0] },
              ],
            },
          ],
        },
      ]),
    ).toThrow(
      "Duplicate API member route 'microsoft.orleans.core/grain-1/methods/getprimarykey-string'",
    );
  });

  test('resolves XML doc IDs using generic arity', () => {
    const generic = pkg.types[0];
    const nonGeneric = {
      ...generic,
      isGeneric: false,
      genericParameters: undefined,
      members: [],
    };
    const types = [nonGeneric, generic];

    expect(findTypeByDocId(types, 'T:Orleans.Grain')).toBe(nonGeneric);
    expect(findTypeByDocId(types, 'T:Orleans.Grain`1')).toBe(generic);
    expect(findTypeByDocId(types.reverse(), 'M:Orleans.Grain`1.GetPrimaryKey')).toBe(
      generic,
    );
  });
});
