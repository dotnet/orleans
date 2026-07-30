import { describe, expect, test } from 'vitest';
import fixture from './fixtures/package-api.json';
import {
  assertUniqueApiRoutes,
  findTypeByDocId,
  findTypeTargetByDocId,
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
    const memberRoutes = buildMemberRoutes([pkg]);
    const identitySlug = memberRoutes[0].memberSlug;
    const getPrimaryKeySlug = memberRoutes[1].memberSlug;

    expect(buildPackageRoutes([pkg]).map((route) => route.packageSlug)).toEqual([
      'microsoft.orleans.core',
    ]);
    expect(buildTypeRoutes([pkg]).map((route) => route.typeSlug)).toEqual([
      'orleans.grain-1',
      'orleans.grainstatus',
    ]);
    expect(
      buildMemberKindRoutes([pkg]).map((route) => route.memberKindSlug),
    ).toEqual(['properties', 'methods']);
    expect(typePath(pkg.package.name, pkg.types[0])).toBe(
      '/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/',
    );
    expect(memberKindPath(pkg.package.name, pkg.types[0], 'method')).toBe(
      '/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/methods/',
    );
    expect(identitySlug).toMatch(/^identitystring-[0-9a-f]{8}$/);
    expect(getPrimaryKeySlug).toMatch(/^getprimarykey-system-string-[0-9a-f]{8}$/);
    expect(memberRoutes[1]).toMatchObject({
      packageSlug: 'microsoft.orleans.core',
      typeSlug: 'orleans.grain-1',
      memberKindSlug: 'methods',
      memberSlug: getPrimaryKeySlug,
    });
    expect(
      buildMemberRoutes([pkg])[1].member.name,
    ).toBe('GetPrimaryKey');
    expect(
      `${memberKindPath(pkg.package.name, pkg.types[0], 'method')}${getPrimaryKeySlug}/`,
    ).toBe(
      `/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/methods/${getPrimaryKeySlug}/`,
    );
  });

  test('builds a package-aware Starlight sidebar', () => {
    const root = buildApiSidebar([pkg]);
    expect(root).toEqual([
      { label: 'API packages', link: '/docs/api/csharp/' },
      {
        label: 'Microsoft.Orleans.Core',
        link: '/docs/api/csharp/microsoft.orleans.core/',
      },
    ]);

    const sidebar = buildApiSidebar([pkg], pkg.package.name);
    expect(sidebar).toHaveLength(2);
    expect(sidebar[0]).toEqual({ label: 'API packages', link: '/docs/api/csharp/' });
    expect(JSON.stringify(sidebar)).toContain(
      '/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/',
    );
    expect(JSON.stringify(sidebar)).not.toContain('/methods/');

    const contextual = buildApiSidebar([pkg], pkg.package.name, pkg.types[0]);
    expect(contextual).toHaveLength(2);
    expect(JSON.stringify(contextual)).toContain('Package overview');
    expect(JSON.stringify(contextual)).not.toContain('GrainStatus');
  });

  test('fails deterministic generation on route collisions', () => {
    const duplicateMemberSlug = buildMemberRoutes([pkg])[1].memberSlug;
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
    ).toThrow("Duplicate API type route 'microsoft.orleans.core/orleans.grain-1'");

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
      `Duplicate API member route 'microsoft.orleans.core/orleans.grain-1/methods/${duplicateMemberSlug}'`,
    );
  });

  test('disambiguates generic overloads with identical parameter types', () => {
    const method = pkg.types[0].members![0];
    const overloaded = {
      ...pkg,
      types: [
        {
          ...pkg.types[0],
          members: [
            {
              ...method,
              name: 'ConfigureFormatter',
              genericParameters: [{ name: 'TOptions' }],
            },
            {
              ...method,
              name: 'ConfigureFormatter',
              genericParameters: [{ name: 'TOptions' }, { name: 'TFormatter' }],
            },
          ],
        },
      ],
    };

    const slugs = buildMemberRoutes([overloaded]).map((route) => route.memberSlug);
    expect(slugs[0]).toMatch(/^configureformatter-1-system-string-[0-9a-f]{8}$/);
    expect(slugs[1]).toMatch(/^configureformatter-2-system-string-[0-9a-f]{8}$/);
    expect(slugs[0]).not.toBe(slugs[1]);
    expect(() => assertUniqueApiRoutes([overloaded])).not.toThrow();
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

  test('resolves XML doc IDs across package boundaries', () => {
    const targetPackage: PackageApiDocument = {
      ...pkg,
      package: { ...pkg.package, name: 'Microsoft.Orleans.Core.Abstractions' },
      types: [
        {
          name: 'GrainType',
          fullName: 'Orleans.Runtime.GrainType',
          namespace: 'Orleans.Runtime',
          kind: 'struct',
        },
      ],
    };

    expect(
      findTypeTargetByDocId(
        [pkg, targetPackage],
        'T:Orleans.Runtime.GrainType',
        pkg.package.name,
      ),
    ).toMatchObject({
      packageName: 'Microsoft.Orleans.Core.Abstractions',
      type: { name: 'GrainType' },
    });
  });
});
