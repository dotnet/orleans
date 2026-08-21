import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import { markdownDirectiveProtectedLineRanges } from '../scripts/lib/markdown-ranges.mjs';
import {
  auditDocumentationContent,
  collectPackageProjects,
  collectCsharpFences,
  findReleaseVersionIssues,
  isNavigationHidden,
  parseDocumentedPackageTable,
  validateCsharpFences,
  validatePackageInventory,
  validateNavigation,
} from '../scripts/lib/source-quality.mjs';

const temporaryDirectories = [];

async function documentationFixture() {
  const repositoryRoot = await mkdtemp(path.join(os.tmpdir(), 'orleans-source-quality-'));
  temporaryDirectories.push(repositoryRoot);
  const siteRoot = path.join(repositoryRoot, 'docs', 'site');
  const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
  await mkdir(sourceRoot, { recursive: true });
  return { repositoryRoot, siteRoot, sourceRoot };
}

afterEach(async () => {
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe('documentation source quality', () => {
  test('uses evaluated package metadata instead of raw conditional XML', async () => {
    const repositoryRoot = await mkdtemp(path.join(os.tmpdir(), 'orleans-package-policy-'));
    temporaryDirectories.push(repositoryRoot);
    const projectRoot = path.join(repositoryRoot, 'src', 'Conditional');
    await mkdir(projectRoot, { recursive: true });
    const project = path.join(projectRoot, 'Conditional.csproj');
    await writeFile(
      project,
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <!-- <IsPackable>false</IsPackable> -->',
        '  <PropertyGroup Condition="\'$(Ignored)\' == \'true\'">',
        '    <PackageId>Wrong.Package</PackageId>',
        '  </PropertyGroup>',
        '</Project>',
      ].join('\n'),
    );

    const packages = await collectPackageProjects(repositoryRoot, {
      evaluate: async (file) => {
        expect(file).toBe(project);
        return {
          IsPackable: 'true',
          PackageId: 'Microsoft.Orleans.Conditional',
          VersionSuffix: 'alpha.1',
        };
      },
    });

    expect(packages).toEqual(
      new Map([
        [
          'Microsoft.Orleans.Conditional',
          { file: project, alpha: true },
        ],
      ]),
    );
  });

  test('allows explicit releases only in migration or upgrade paths and links', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'migration/1-to-10.md',
        source: 'Upgrade from Orleans 1.x or Orleans 2.x to Orleans 10.',
      }),
    ).toEqual([]);
    expect(
      findReleaseVersionIssues({
        relativePath: 'deployment/upgrades.md',
        source: 'Upgrade Orleans 9 to Orleans 10.',
      }),
    ).toEqual([]);
    expect(
      findReleaseVersionIssues({
        relativePath: 'deployment/current.md',
        source: 'See [Upgrade from Orleans 9](../migration-guide.md#orleans-9).',
      }),
    ).toEqual([]);
  });

  test('rejects release branding across rendered Markdown formatting and soft breaks', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: [
          'Orleans **10** is current.',
          'Orleans *v10* is current.',
          'Orleans',
          '10 is current.',
        ].join('\n'),
      }),
    ).toEqual([
      expect.objectContaining({ rule: 'DOCS001', line: 1 }),
      expect.objectContaining({ rule: 'DOCS001', line: 2 }),
      expect.objectContaining({ rule: 'DOCS001', line: 3 }),
    ]);
  });

  test('rejects forward and reverse Orleans release/version formulations', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: [
          'Orleans release 10 is current.',
          'Orleans **release 10** is current.',
          'Version 10 of Orleans is current.',
          'Release **10** of Orleans is current.',
          'Version 10.0.1 of Orleans is current.',
          'Version 10 of Microsoft Orleans is current.',
          'Release **10.0.1** of Microsoft\nOrleans is current.',
        ].join('\n'),
      }),
    ).toEqual([
      expect.objectContaining({ rule: 'DOCS001', line: 1 }),
      expect.objectContaining({ rule: 'DOCS001', line: 2 }),
      expect.objectContaining({ rule: 'DOCS001', line: 3 }),
      expect.objectContaining({ rule: 'DOCS001', line: 4 }),
      expect.objectContaining({ rule: 'DOCS001', line: 5 }),
      expect.objectContaining({ rule: 'DOCS001', line: 6 }),
      expect.objectContaining({ rule: 'DOCS001', line: 7 }),
    ]);
  });

  test('rejects release branding in rendered HTML', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: '<div>Orleans <strong>10</strong> is current.</div>',
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 1,
        message: expect.stringContaining("'Orleans 10'"),
      }),
    ]);
  });

  test('does not treat HTML metadata, comments, or code examples as rendered branding', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: [
          '<div aria-label="Orleans 10"><!-- Orleans 10 --><code>Orleans 10</code></div>',
          '<pre>Orleans 10</pre>',
        ].join('\n'),
      }),
    ).toEqual([]);
  });

  test('reports release branding in frontmatter on the exact source line', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: '---\ntitle: Current\ndescription: Orleans 10 guidance.\n---\n# Current',
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 3,
        message: expect.stringContaining("'Orleans 10'"),
      }),
    ]);
  });

  test('does not exempt release branding in links to ordinary pages', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: 'See [Orleans 10 configuration](configuration.md).',
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 1,
        message: expect.stringContaining("'Orleans 10'"),
      }),
    ]);
  });

  test.each([
    '[Orleans 10](../migration/../host/configuration.md)',
    '[Orleans 10](configuration.md?next=/migration/9-to-10.md)',
  ])('does not exempt a migration-looking link to an ordinary page: %s', (source) => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 1,
        message: expect.stringContaining("'Orleans 10'"),
      }),
    ]);
  });

  test('allows a reference-style link to migration guidance', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source: [
          'See [Upgrade Orleans 9][upgrade].',
          '',
          '[upgrade]: ../migration/9-to-10.md',
        ].join('\n'),
      }),
    ).toEqual([]);
  });

  test('does not exempt a normalized ordinary source path containing a migration segment', () => {
    expect(
      findReleaseVersionIssues({
        relativePath: 'migration/../host/current.md',
        source: 'Orleans 10 is current.',
      }),
    ).toHaveLength(1);
  });

  test('rejects every explicit Orleans release in ordinary documentation', () => {
    const issues = findReleaseVersionIssues({
      relativePath: 'host/configuration.md',
      source: [
        'Orleans 1.x is the recommended version.',
        'Orleans 2.0 is the recommended version.',
        'Orleans 3.x is the recommended version.',
        'Orleans 7 is the recommended version.',
        'Orleans 8.2 is the recommended version.',
        'Orleans 9 is the recommended version.',
        'Orleans version: 6 is the recommended version.',
        ':::zone target="docs" pivot="orleans-2-x"',
        'Orleans 10 is current.',
        'Orleans 10.x is current.',
        'Orleans v10 is current.',
        ':::zone target="docs" pivot="orleans-10-0"',
      ].join('\n'),
    });

    expect(issues).toHaveLength(12);
    expect(issues[0]).toMatchObject({ rule: 'DOCS001', line: 1 });
    expect(issues[11]).toMatchObject({ rule: 'DOCS001', line: 12 });
    expect(issues[8].message).toContain("'Orleans 10'");
    expect(issues[9].message).toContain("'Orleans 10.x'");
    expect(issues[10].message).toContain("'Orleans v10'");
    expect(issues[11].message).toContain("'orleans-10-0'");
    expect(issues[0].message).toContain('current-release/versionless documentation');
    expect(issues[0].remediation).toContain('migration/');
    expect(issues[0].remediation).not.toContain('Orleans 10');
  });

  test('allows versionless current behavior and unrelated version-like values', () => {
    const source = [
      'Orleans is current.',
      'Microsoft.Orleans.Server 9.2.1 is a package fact.',
      '<PackageReference Include="Microsoft.Orleans.Server" Version="10.0.0" />',
      'Target net10.0 for this example.',
      'Orleans.Runtime.GrainInterfaceVersion is an API identifier.',
      'Orleans10 is an API identifier.',
      'Orleans10.Create() is an API call.',
      '<xref:Example.Orleans10> is an API reference.',
      'Grain interface version 2 is supported.',
      'Package version 2.0.0 was published on 08/02/2026.',
      'Release 10 of Microsoft.Orleans.Server is a package fact.',
      'Version 10 of Microsoft.Orleans is a namespace fact.',
      'Release 10 of Microsoft OrleansRuntime is an API fact.',
      'Version 10 of Orleans.Runtime is an API fact.',
      'Version 10 of .NET is unrelated.',
      '.NET 9 can host an unrelated component.',
      'Orleans:',
      '1. Performs the first current routing step.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/current.md',
        source,
      }),
    ).toEqual([]);
  });

  test('rejects release branding in an explicitly declared compatibility zone', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0"',
      'This behavior applied to Orleans 9.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-9-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 5,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    ]);
  });

  test.each(['\n', '\r\n'])(
    'does not open a compatibility exemption from list-contained indented code using %j',
    (newline) => {
      const source = [
        '---',
        'zone_pivot_groups: orleans-version',
        '---',
        '- Example:',
        '',
        '      :::zone target="docs" pivot="orleans-9-0"',
        '',
        'Orleans 9 is current prose.',
      ].join(newline);

      expect(
        findReleaseVersionIssues({
          relativePath: 'host/compatibility.md',
          source,
        }),
      ).toEqual([
        {
          rule: 'DOCS001',
          file: 'host/compatibility.md',
          line: 8,
          message:
            "Explicit Orleans release reference 'Orleans 9' appears in current-release/versionless documentation.",
          remediation:
            'Describe current behavior without an Orleans release number, or move version-specific guidance into migration/ or upgrade documentation and link to it.',
        },
      ]);
    },
  );

  test.each([
    ['lazy blockquote', '> Literal directive:\n:::code source="Missing.cs"'],
    ['non-lazy blockquote heading', '> # Heading\n:::code source="Active.cs"'],
    ['processing instruction', '<?docs\n:::code source="Missing.cs"\n?>'],
    ['custom HTML block', '<custom-element>\n:::code source="Missing.cs"\n'],
    [
      'custom HTML block with a quoted greater-than character',
      '<custom-element title="a > b">\n:::code source="Missing.cs"\n',
    ],
    [
      'tab-indented list fence',
      '- Example:\n\t```text\n\t:::code source="Missing.cs"\n\t```',
    ],
  ])('dependency-free protected ranges match the Markdown parser for %s', (_name, source) => {
    expect(
      markdownDirectiveProtectedLineRanges(source, { dependencyFree: true }),
    ).toEqual(markdownDirectiveProtectedLineRanges(source));
  });

  test('ignores fake compatibility directives in list-contained fenced code', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      '- Example:',
      '',
      '  ```text',
      '  :::zone target="docs" pivot="orleans-9-0"',
      '  :::zone-end',
      '  ```',
      '',
      'Orleans 8 is current prose.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 11,
        message: expect.stringContaining("'Orleans 8'"),
      }),
    ]);
  });

  test('ignores fake compatibility directives in blockquote and HTML examples', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      '> ```text',
      '> :::zone target="docs" pivot="orleans-9-0"',
      '> :::zone-end',
      '> ```',
      '',
      '<pre>',
      ':::zone target="docs" pivot="orleans-8-0"',
      ':::zone-end',
      '</pre>',
      '',
      'Orleans 7 is current prose.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 10,
        message: expect.stringContaining("'orleans-8-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 14,
        message: expect.stringContaining("'Orleans 7'"),
      }),
    ]);
  });

  test('ignores a fake close in list-contained indented code while reporting rendered releases', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0"',
      '- Example:',
      '',
      '      :::zone-end',
      '',
      'This behavior applied to Orleans 9.',
      ':::zone-end',
      'Orleans 8 is current prose.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-9-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 9,
        message: expect.stringContaining("'Orleans 9'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 11,
        message: expect.stringContaining("'Orleans 8'"),
      }),
    ]);
  });

  test.each([
    ['fenced', '```text\n:::zone target="docs" pivot="orleans-9-0"\n```'],
    ['indented', '    :::zone target="docs" pivot="orleans-9-0"\n'],
    ['blockquote code', '>     :::zone target="docs" pivot="orleans-9-0"\n>'],
  ])('does not open a compatibility exemption from %s content', (_kind, fakeOpening) => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      fakeOpening,
      'Orleans 9 is current prose.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        message: expect.stringContaining("'Orleans 9'"),
      }),
    ]);
  });

  test.each(['-->', '--!>'])(
    'does not open a compatibility exemption from an HTML comment closed by %s',
    (commentClose) => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      '<!--',
      ':::zone target="docs" pivot="orleans-9-0"',
      commentClose,
      'Orleans 9 is current prose.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toContainEqual(
      expect.objectContaining({
        rule: 'DOCS001',
        line: 7,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    );
    },
  );

  test('ignores a fake close in fenced code while reporting rendered releases', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0"',
      '```text',
      ':::zone-end',
      '```',
      'This behavior applied to Orleans 9.',
      ':::zone-end',
      'Orleans 8 is unrelated current prose.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-9-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 8,
        message: expect.stringContaining("'Orleans 9'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 10,
        message: expect.stringContaining("'Orleans 8'"),
      }),
    ]);
  });

  test('does not exempt nested compatibility zones which cannot be rendered safely', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0"',
      'Orleans 9 behavior.',
      ':::zone target="docs" pivot="orleans-8-0"',
      'Orleans 8 behavior.',
      ':::zone-end',
      'Orleans 7 behavior.',
      ':::zone-end',
      'Orleans 6 is unrelated current prose.',
    ].join('\n');

    const issues = findReleaseVersionIssues({
      relativePath: 'host/compatibility.md',
      source,
    });
    for (const [line, version] of [
      [5, 'Orleans 9'],
      [7, 'Orleans 8'],
      [9, 'Orleans 7'],
      [11, 'Orleans 6'],
    ]) {
      expect(issues).toContainEqual(
        expect.objectContaining({
          rule: 'DOCS001',
          line,
          message: expect.stringContaining(`'${version}'`),
        }),
      );
    }
  });

  test('does not let an unclosed compatibility zone exempt the rest of the file', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0"',
      'Orleans 9 behavior.',
      '',
      'Unrelated guidance still mentions Orleans 8.',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          rule: 'DOCS001',
          line: 7,
          message: expect.stringContaining("'Orleans 8'"),
        }),
      ]),
    );
  });

  test('does not treat zone-like frontmatter text as authored directives', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      'description: |',
      '  :::zone target="docs" pivot="orleans-9-0"',
      '  Orleans 9 is current metadata.',
      '  :::zone-end',
      '---',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toContainEqual(
      expect.objectContaining({
        rule: 'DOCS001',
        line: 5,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    );
  });

  test('does not exempt malformed compatibility zones', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-9-0" unexpected="value"',
      'Orleans 9 behavior.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          rule: 'DOCS001',
          line: 5,
          message: expect.stringContaining("'Orleans 9'"),
        }),
      ]),
    );
  });

  test('does not treat body prose as compatibility exemption metadata', () => {
    const source = [
      'zone_pivot_groups: orleans-version',
      ':::zone target="docs" pivot="orleans-9-0"',
      'This behavior applied to Orleans 9.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toHaveLength(2);
  });

  test('does not exempt compatibility zones when frontmatter is malformed or missing', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      ':::zone target="docs" pivot="orleans-9-0"',
      'This behavior applied to Orleans 9.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toHaveLength(2);
  });

  test('rejects a current-release compatibility pivot and versioned prose', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-10-0"',
      'Orleans 9 is current.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-10-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 5,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    ]);
  });

  test('rejects every release in a mixed compatibility pivot and versioned prose', () => {
    const source = [
      '---',
      'zone_pivot_groups: orleans-version',
      '---',
      ':::zone target="docs" pivot="orleans-10-0,orleans-9-0"',
      'Orleans 9 is current.',
      ':::zone-end',
    ].join('\n');

    expect(
      findReleaseVersionIssues({
        relativePath: 'host/compatibility.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-10-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 4,
        message: expect.stringContaining("'orleans-9-0'"),
      }),
      expect.objectContaining({
        rule: 'DOCS001',
        line: 5,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    ]);
  });

  test('audits historical pivots and C# fences in external and nested includes once', async () => {
    const fixture = await documentationFixture();
    const includes = path.join(fixture.siteRoot, 'src', 'content', 'includes');
    await mkdir(includes, { recursive: true });
    const guide = path.join(fixture.sourceRoot, 'guide.md');
    const external = path.join(includes, 'external.md');
    const nested = path.join(includes, 'nested.md');
    await writeFile(guide, '[!INCLUDE [external](../includes/external.md)]\n');
    await writeFile(
      external,
      ':::zone target="docs" pivot="orleans-9-0"\n[!INCLUDE [nested](nested.md)]\n',
    );
    await writeFile(nested, '```csharp\nConsole.WriteLine("uncompiled");\n```\n');
    const result = await auditDocumentationContent({
      ...fixture,
      markdownFiles: [guide],
    });

    expect(result.issues).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          rule: 'DOCS001',
          file: 'docs/site/src/content/includes/external.md',
          line: 1,
        }),
        expect.objectContaining({
          rule: 'DOCS004',
          file: 'docs/site/src/content/includes/nested.md',
          line: 1,
        }),
      ]),
    );
    expect(result.auditedMarkdown.filter((item) => item.file === nested)).toHaveLength(1);
  });

  test('reports circular includes without recursively auditing files more than once', async () => {
    const fixture = await documentationFixture();
    const guide = path.join(fixture.sourceRoot, 'guide.md');
    const nested = path.join(fixture.sourceRoot, 'nested.md');
    await writeFile(guide, '[!INCLUDE [nested](nested.md)]\n');
    await writeFile(nested, '---\ntitle: Nested\n---\n[!INCLUDE [guide](guide.md)]\n');

    const result = await auditDocumentationContent({
      ...fixture,
      markdownFiles: [guide],
    });

    expect(result.issues).toContainEqual(
      expect.objectContaining({
        rule: 'DOCS006',
        file: 'nested.md',
        line: 4,
        message: expect.stringContaining('Circular INCLUDE'),
      }),
    );
    expect(result.auditedMarkdown).toHaveLength(2);
  });

  test('rejects includes which traverse outside the docs site tree', async () => {
    const fixture = await documentationFixture();
    const guide = path.join(fixture.sourceRoot, 'guide.md');
    await writeFile(guide, '[!INCLUDE [outside](../../../../outside.md)]\n');

    const result = await auditDocumentationContent({
      ...fixture,
      markdownFiles: [guide],
    });

    expect(result.issues).toContainEqual(
      expect.objectContaining({
        rule: 'DOCS006',
        file: 'guide.md',
        line: 1,
        message: expect.stringContaining('outside the allowed documentation tree'),
      }),
    );
  });

  test.each(['\n', '\r\n'])(
    'ignores include directives in Markdown literal ranges using %j',
    async (newline) => {
      const fixture = await documentationFixture();
      const guide = path.join(fixture.sourceRoot, 'guide.md');
      const active = path.join(fixture.sourceRoot, 'active.md');
      await writeFile(active, 'Active include.\n');
      await writeFile(
        guide,
        [
          '````markdown',
          '```text',
          '[!INCLUDE [missing](missing.md)]',
          '```',
          '````',
          '~~~text',
          '[!INCLUDE [missing](missing.md)]',
          '~~~',
          '1. Literal example:',
          '',
          '   ```text',
          '   [!INCLUDE [missing](missing.md)]',
          '   ```',
          '       [!INCLUDE [missing](missing.md)]',
          '> [!INCLUDE [missing](missing.md)]',
          'Use `[!INCLUDE [missing](missing.md)]` literally.',
          '<pre>',
          '[!INCLUDE [missing](missing.md)]',
          '</pre>',
          '> [!NOTE]',
          '> [!INCLUDE [missing](missing.md)]',
          '',
          '> [!TIP]',
          '> Literal include:',
          '[!INCLUDE [missing](missing.md)]',
          '',
          '[!INCLUDE [active](active.md)]',
          '',
        ].join(newline),
      );

      const result = await auditDocumentationContent({
        ...fixture,
        markdownFiles: [guide],
      });

      expect(result.issues).toEqual([]);
      expect(result.auditedMarkdown.map((document) => document.file)).toEqual(
        expect.arrayContaining([path.resolve(guide), path.resolve(active)]),
      );
    },
  );

  test('keeps triple-backtick content inside a four-backtick fence', () => {
    const issues = findReleaseVersionIssues({
      relativePath: 'host/fences.md',
      source: [
        '````csharp',
        '```',
        'Orleans 8 is shown as code.',
        '```',
        '````',
        'Orleans 9 is current.',
      ].join('\n'),
    });

    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ rule: 'DOCS001', line: 6 });
  });

  test('recognizes tilde, ordinary triple, and longer valid fence closes', () => {
    for (const source of [
      '~~~text\nOrleans 9 is code.\n~~~\nOrleans 8 is current.',
      '```text\nOrleans 9 is code.\n```\nOrleans 8 is current.',
      '```text\nOrleans 9 is code.\n`````\nOrleans 8 is current.',
    ]) {
      const issues = findReleaseVersionIssues({
        relativePath: 'host/fences.md',
        source,
      });
      expect(issues).toHaveLength(1);
      expect(issues[0]).toMatchObject({ rule: 'DOCS001', line: 4 });
    }
  });

  test('reports missing, duplicate, and nonexistent navigation targets', () => {
    const issues = validateNavigation({
      markdownPaths: [
        'overview.md',
        'missing.md',
        'implementation/index.md',
        'grains/event-sourcing/index.md',
      ],
      excludedPaths: [],
      tocItems: [
        { name: 'Overview', href: 'overview.md' },
        { name: 'Duplicate overview', href: 'overview.md' },
        { name: 'Gone', href: 'gone.md' },
        {
          name: 'Architecture and internals',
          items: [{ name: 'Runtime', href: 'implementation/index.md' }],
        },
        {
          name: 'Event Sourcing',
          items: [{ name: 'Overview', href: 'grains/event-sourcing/index.md' }],
        },
      ],
    });

    expect(issues.map((issue) => issue.message)).toEqual(
      expect.arrayContaining([
        "Navigation target 'gone.md' does not exist.",
        'Maintained conceptual page is missing from toc.yml.',
        "Navigation target 'overview.md' appears 2 times.",
      ]),
    );
  });

  test('recognizes pages retained outside navigation', () => {
    expect(
      isNavigationHidden(
        '---\ntitle: Compatibility page\nnavigation: hidden\n---\n\n# Compatibility page\n',
      ),
    ).toBe(true);
    expect(isNavigationHidden('---\ntitle: Current page\n---\n\n# Current page\n')).toBe(false);
  });

  test('requires durable architecture and Event Sourcing navigation sections', () => {
    const issues = validateNavigation({
      markdownPaths: [],
      excludedPaths: [],
      tocItems: [],
    });

    expect(issues.filter((issue) => issue.rule === 'DOCS003')).toHaveLength(2);
  });

  test('counts anchored navigation entries against their Markdown target', () => {
    const issues = validateNavigation({
      markdownPaths: ['page.md'],
      excludedPaths: [],
      tocItems: [
        { name: 'Page', href: 'page.md' },
        { name: 'Page section', href: 'page.md#details' },
      ],
    });

    expect(issues.some((issue) => issue.message.includes('appears 2 times'))).toBe(true);
  });

  test('rejects inline C# fences and points authors to compiled regions', () => {
    const pages = [
      {
        relativePath: 'example.md',
        source: '# Example\n\n```csharp\nConsole.WriteLine("Hello");\n```\n',
      },
    ];

    expect(collectCsharpFences(pages[0].source)).toHaveLength(1);
    expect(validateCsharpFences(pages)).toEqual([
      expect.objectContaining({
        rule: 'DOCS004',
        file: 'example.md',
        line: 3,
        remediation: expect.stringContaining(':::code'),
      }),
    ]);
    expect(collectCsharpFences(pages[0].source.replaceAll('\n', '\r\n'))).toEqual(
      collectCsharpFences(pages[0].source),
    );
  });

  test('recognizes C# fence metadata and whitespace', () => {
    expect(collectCsharpFences('``` csharp\nA();\n```\n')).toHaveLength(1);
    expect(collectCsharpFences('```csharp title=demo\nB();\n```\n')).toHaveLength(1);
  });

  test('uses consistent DOCS001 offsets for long CRLF fenced content', () => {
    const source = [
      '```csharp',
      ...Array.from({ length: 30 }, () => 'Orleans 8 is literal code.'),
      '```',
      'Orleans 9 is ordinary guidance.',
    ].join('\r\n');

    const issues = findReleaseVersionIssues({ relativePath: 'example.md', source });
    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ rule: 'DOCS001', line: 33 });
  });

  test('does not treat a four-space-indented backtick line as a fence', () => {
    const source = [
      '    ```',
      '    This is indented code.',
      '',
      'Orleans 9 is current.',
    ].join('\n');

    expect(collectCsharpFences(source)).toEqual([]);
    expect(
      findReleaseVersionIssues({
        relativePath: 'host/indented-code.md',
        source,
      }),
    ).toEqual([
      expect.objectContaining({
        rule: 'DOCS001',
        file: 'host/indented-code.md',
        line: 4,
        message: expect.stringContaining("'Orleans 9'"),
      }),
    ]);
  });

  test('rejects blockquote, list, tilde, and ordinary C# fences', () => {
    const pages = [
      {
        relativePath: 'fence-shapes.md',
        source: [
          '> ```csharp',
          '> Orleans 8 is current.',
          '> ```',
          '',
          '1. ```cs',
          '   Orleans 8 is current.',
          '   ```',
          '',
          '~~~c#',
          'Orleans 8 is current.',
          '~~~',
          '',
          '```csharp',
          'Orleans 8 is current.',
          '```',
        ].join('\n'),
      },
    ];
    expect(collectCsharpFences(pages[0].source).map((fence) => fence.line)).toEqual([1, 5, 9, 13]);
    expect(
      findReleaseVersionIssues({
        relativePath: pages[0].relativePath,
        source: pages[0].source,
      }),
    ).toEqual([]);
    expect(validateCsharpFences(pages)).toEqual(
      [1, 5, 9, 13].map((line) =>
        expect.objectContaining({
          rule: 'DOCS004',
          file: 'fence-shapes.md',
          line,
        }),
      ),
    );
  });

  test.each([
    ['backticks', '````text', '```csharp', '```', '````'],
    ['tildes', '~~~~text', '~~~csharp', '~~~', '~~~~'],
  ])(
    'keeps shorter %s delimiters inside a longer outer fence',
    (_name, outerOpen, innerOpen, innerClose, outerClose) => {
      const source = [
        outerOpen,
        innerOpen,
        'Orleans 8 is current.',
        innerClose,
        outerClose,
        'Orleans 9 is current.',
      ].join('\n');

      expect(collectCsharpFences(source)).toEqual([]);
      expect(
        findReleaseVersionIssues({
          relativePath: 'host/outer-fence.md',
          source,
        }),
      ).toEqual([
        expect.objectContaining({
          rule: 'DOCS001',
          file: 'host/outer-fence.md',
          line: 6,
          message: expect.stringContaining("'Orleans 9'"),
        }),
      ]);
    },
  );

  test.each([
    [
      'canonical NuGet link',
      '[Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Server)',
      '',
    ],
    [
      'NuGet link without www',
      '[Microsoft.Orleans.Server](https://nuget.org/packages/Microsoft.Orleans.Server)',
      '',
    ],
    [
      'reference-style NuGet link',
      '[Microsoft.Orleans.Server][server]',
      '\n[server]: https://www.nuget.org/packages/Microsoft.Orleans.Server',
    ],
    ['plain code', '`Microsoft.Orleans.Server`', ''],
    ['plain text', 'Microsoft.Orleans.Server', ''],
  ])('uses the displayed package identity from a %s cell', (_name, cell, references) => {
    const source = [
      '| Package | Purpose |',
      '| --- | --- |',
      `| ${cell} | Hosting |`,
      references,
    ].join('\n');
    const parsed = parseDocumentedPackageTable(source);

    expect([...parsed.documentedPackages]).toEqual([['Microsoft.Orleans.Server', 3]]);
    expect(parsed.issues).toEqual([]);
    expect(
      validatePackageInventory({
        packages: new Map([['Microsoft.Orleans.Server', {}]]),
        documentedPackages: parsed.documentedPackages,
      }),
    ).toEqual([]);
  });

  test('ignores package rows and reference definitions outside rendered tables', () => {
    const parsed = parseDocumentedPackageTable([
      '```markdown',
      '| Package | Purpose |',
      '| --- | --- |',
      '| [Microsoft.Orleans.Fake][fake] | Hidden |',
      '[server]: https://www.nuget.org/packages/Microsoft.Orleans.Server',
      '```',
      '<!--',
      '| Package | Purpose |',
      '| --- | --- |',
      '| Microsoft.Orleans.Commented | Hidden |',
      '-->',
      '| Package | Purpose |',
      '| --- | --- |',
      '| [Microsoft.Orleans.Server][server] | Hosting |',
    ].join('\n'));

    expect([...parsed.documentedPackages]).toEqual([['Microsoft.Orleans.Server', 14]]);
    expect(parsed.issues).toEqual([
      expect.objectContaining({
        rule: 'DOCS005',
        line: 14,
        message: expect.stringContaining("uses unresolved reference 'server'"),
      }),
    ]);
  });

  test('rejects raw HTML package links which bypass Markdown target validation', () => {
    const parsed = parseDocumentedPackageTable([
      '| Package | Purpose |',
      '| --- | --- |',
      '| <a href="https://example.com">Microsoft.Orleans.Server</a> | Hosting |',
    ].join('\n'));

    expect([...parsed.documentedPackages]).toEqual([['Microsoft.Orleans.Server', 3]]);
    expect(parsed.issues).toEqual([
      expect.objectContaining({
        rule: 'DOCS005',
        line: 3,
        message: expect.stringContaining('uses raw HTML and cannot be validated'),
      }),
    ]);
  });

  test.each([
    [
      'canonical link',
      '[Microsoft.Orleans.Server](https://www.nuget.org/packages/Microsoft.Orleans.Client)',
      '',
    ],
    [
      'link without www',
      '[Microsoft.Orleans.Server](https://nuget.org/packages/Microsoft.Orleans.Client)',
      '',
    ],
    [
      'reference-style link',
      '[Microsoft.Orleans.Server][client]',
      '\n[client]: https://www.nuget.org/packages/Microsoft.Orleans.Client',
    ],
  ])('rejects a displayed package identity which disagrees with its %s target', (_name, cell, reference) => {
    const parsed = parseDocumentedPackageTable([
      '| Package | Purpose |',
      '| --- | --- |',
      `| ${cell} | Hosting |`,
      reference,
    ].join('\n'));

    expect([...parsed.documentedPackages]).toEqual([['Microsoft.Orleans.Server', 3]]);
    expect(parsed.issues).toEqual([
      expect.objectContaining({
        rule: 'DOCS005',
        file: 'resources/nuget-packages.md',
        line: 3,
        message: expect.stringContaining(
          "Displayed package 'Microsoft.Orleans.Server' does not match NuGet link target 'Microsoft.Orleans.Client'",
        ),
      }),
    ]);
  });

  test.each([
    [
      'non-NuGet target',
      '[Microsoft.Orleans.Server](https://example.com/packages/Microsoft.Orleans.Server)',
      '',
      'does not target a NuGet package page',
    ],
    [
      'unresolved reference',
      '[Microsoft.Orleans.Server][server]',
      '',
      "uses unresolved reference 'server'",
    ],
  ])('rejects a package link with a %s', (_name, cell, reference, message) => {
    const parsed = parseDocumentedPackageTable([
      '| Package | Purpose |',
      '| --- | --- |',
      `| ${cell} | Hosting |`,
      reference,
    ].join('\n'));

    expect([...parsed.documentedPackages]).toEqual([['Microsoft.Orleans.Server', 3]]);
    expect(parsed.issues).toEqual([
      expect.objectContaining({
        rule: 'DOCS005',
        file: 'resources/nuget-packages.md',
        line: 3,
        message: expect.stringContaining(message),
      }),
    ]);
  });

  test.each([
    [
      'canonical link',
      '[Microsoft.Orleans.Fake](https://www.nuget.org/packages/Microsoft.Orleans.Server)',
      '',
    ],
    [
      'link without www',
      '[Microsoft.Orleans.Fake](https://nuget.org/packages/Microsoft.Orleans.Server)',
      '',
    ],
    [
      'reference-style link',
      '[Microsoft.Orleans.Fake][fake]',
      '\n[fake]: https://www.nuget.org/packages/Microsoft.Orleans.Server',
    ],
    [
      'noncanonical link',
      '[Microsoft.Orleans.Fake](https://example.com/packages/Microsoft.Orleans.Server)',
      '',
    ],
  ])('rejects a fake displayed package using a %s', (_name, cell, references) => {
    const parsed = parseDocumentedPackageTable([
      '| Package | Purpose |',
      '| --- | --- |',
      `| ${cell} | Not real |`,
      references,
    ].join('\n'));
    const issues = validatePackageInventory({
      packages: new Map([['Microsoft.Orleans.Server', {}]]),
      documentedPackages: parsed.documentedPackages,
    });

    expect(issues).toContainEqual(
      expect.objectContaining({
        rule: 'DOCS005',
        file: 'resources/nuget-packages.md',
        line: 3,
        message: "Documented package 'Microsoft.Orleans.Fake' has no packable source project.",
      }),
    );
  });

  test('parses stream package status from linked package cells', () => {
    const parsed = parseDocumentedPackageTable([
      '| Provider | Package | Status |',
      '| --- | --- | --- |',
      '| Redis | [`Microsoft.Orleans.Streaming.Redis`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.Redis) | **Alpha** |',
      '| Memory | [`Microsoft.Orleans.Streaming`](https://www.nuget.org/packages/Microsoft.Orleans.Streaming) | Stable |',
    ].join('\n'), 1, 2);

    expect(parsed.documentedPackages).toEqual(
      new Map([
        ['Microsoft.Orleans.Streaming.Redis', 3],
        ['Microsoft.Orleans.Streaming', 4],
      ]),
    );
    expect(parsed.documentedStatus).toEqual(
      new Map([
        ['Microsoft.Orleans.Streaming.Redis', 'Alpha'],
        ['Microsoft.Orleans.Streaming', 'Stable'],
      ]),
    );
  });

  test('requires every packable package to be documented or explicitly excluded', () => {
    const packages = new Map([
      ['Microsoft.Orleans.Server', {}],
      ['Microsoft.Orleans.Runtime', {}],
    ]);
    const issues = validatePackageInventory({
      packages,
      documentedPackages: new Set(['Microsoft.Orleans.Server']),
      exclusions: { packages: {} },
    });

    expect(issues.map((issue) => issue.message)).toContain(
      "Packable source package 'Microsoft.Orleans.Runtime' is missing from the documented inventory.",
    );
  });

  test('accepts reasoned package exclusions and detects stale ones after deletion', () => {
    const exclusions = {
      packages: {
        'Microsoft.Orleans.Runtime':
          'Low-level runtime implementation composed by the server metapackage.',
      },
    };
    expect(
      validatePackageInventory({
        packages: new Map([
          ['Microsoft.Orleans.Server', {}],
          ['Microsoft.Orleans.Runtime', {}],
        ]),
        documentedPackages: new Set(['Microsoft.Orleans.Server']),
        exclusions,
      }),
    ).toEqual([]);

    const issues = validatePackageInventory({
      packages: new Map([['Microsoft.Orleans.Server', {}]]),
      documentedPackages: new Set(['Microsoft.Orleans.Server']),
      exclusions,
    });
    expect(issues.map((issue) => issue.message)).toContain(
      "Package inventory exclusion 'Microsoft.Orleans.Runtime' has no packable source project.",
    );
  });
});
