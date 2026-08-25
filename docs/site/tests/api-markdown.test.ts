import { describe, expect, test } from 'vitest';
import fixture from './fixtures/package-api.json';
import {
  markdownResponse,
  renderApiIndexMarkdown,
  renderMemberMarkdown,
  renderMemberKindMarkdown,
  renderPackageMarkdown,
  renderDocNodes,
  renderTypeMarkdown,
} from '../src/lib/api/markdown';
import {
  buildMemberSignature,
  buildTypeSignature,
  memberDisplayName,
  memberSlug,
  nugetHref,
} from '../src/lib/api/packages';
import type { PackageApiDocument } from '../src/lib/api/types';

const pkg = fixture as PackageApiDocument;
const grain = pkg.types[0];
const primaryKeySlug = memberSlug(grain.members![0]);
const sourceRevision = process.env.ORLEANS_DOCS_SOURCE_COMMIT ?? pkg.package.sourceCommit;

describe('native API Markdown companions', () => {
  test('renders package and type navigation with source and NuGet links', () => {
    const index = renderApiIndexMarkdown([pkg], '/orleans/');
    const packageMarkdown = renderPackageMarkdown(pkg, '/orleans/');
    const typeMarkdown = renderTypeMarkdown(pkg, grain, '/orleans/');

    expect(index).toContain(
      '[Microsoft.Orleans.Core](/orleans/docs/api/csharp/microsoft.orleans.core/)',
    );
    expect(packageMarkdown).toContain(
      '[Grain&lt;TState&gt;](/orleans/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/)',
    );
    expect(typeMarkdown).toContain('# Grain&lt;TState&gt;');
    expect(typeMarkdown).toContain(
      'https://www.nuget.org/packages/Microsoft.Orleans.Core',
    );
    expect(typeMarkdown).toContain(
      `https://github.com/dotnet/orleans/blob/${sourceRevision}/src/Orleans.Core.Abstractions/Runtime/Grain.cs#L10-L80`,
    );
    expect(typeMarkdown).toContain(
      'public abstract class Grain<TState> : Grain, IGrainBase',
    );
    expect(typeMarkdown).toContain(
      `/orleans/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/methods/${primaryKeySlug}/`,
    );
  });

  test('omits NuGet links only for explicitly unpublished API assemblies', () => {
    const unpublishedPackage = 'Microsoft.Orleans.UnpublishedFixture';

    expect(
      nugetHref(unpublishedPackage, {
        [unpublishedPackage]: 'Test fixture.',
      }),
    ).toBeUndefined();
    expect(nugetHref(unpublishedPackage, {})).toBe(
      `https://www.nuget.org/packages/${unpublishedPackage}`,
    );
  });

  test('renders member documentation, parameters, returns, exceptions, and examples', () => {
    const markdown = renderMemberKindMarkdown(pkg, grain, 'method', '/orleans/');

    expect(markdown).toContain(`## GetPrimaryKey(String) {#${primaryKeySlug}}`);
    expect(markdown).toContain('### Parameters');
    expect(markdown).toContain('Receives the key extension.');
    expect(markdown).toContain('### Returns');
    expect(markdown).toContain('The grain identifier.');
    expect(markdown).toContain('### Exceptions');
    expect(markdown).toContain('System.InvalidOperationException');
    expect(markdown).toContain('### Examples');
    expect(markdown).toContain('var id = grain.GetPrimaryKey');
    expect(markdown).toContain('#L42-L48');
    expect(markdown).toContain(
      `[Dedicated page](/orleans/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/methods/${primaryKeySlug}/)`,
    );
  });

  test('renders individual member Markdown companions', () => {
    const markdown = renderMemberMarkdown(
      pkg,
      grain,
      grain.members![0],
      '/orleans/',
    );

    expect(markdown).toContain(
      '# Grain&lt;TState&gt;.GetPrimaryKey(String)',
    );
    expect(markdown).toContain(
      '[Methods](/orleans/docs/api/csharp/microsoft.orleans.core/orleans.grain-1/methods/)',
    );
    expect(markdown).not.toContain('[Dedicated page]');
    expect(markdown).toContain('Receives the key extension.');
  });

  test('serves Markdown with an explicit content type', () => {
    const response = markdownResponse('# API\n');
    expect(response.headers.get('Content-Type')).toBe(
      'text/markdown; charset=utf-8',
    );
  });

  test('renders delegate and attribute signatures from generator metadata', () => {
    expect(
      buildTypeSignature({
        name: 'Callback',
        kind: 'delegate',
        accessibility: 'public',
        delegateReturnType: 'System.Threading.Tasks.Task',
        delegateParameters: [{ name: 'value', type: 'System.String' }],
        attributes: [
          {
            name: 'ObsoleteAttribute',
            constructorArguments: ['"Use Handler"'],
          },
        ],
      }),
    ).toBe(
      '[Obsolete("Use Handler")]\npublic delegate Task Callback(String value);',
    );
    expect(
      buildMemberSignature({
        name: 'Run',
        kind: 'method',
        signature: 'public void Worker.Run()',
        attributes: [{ name: 'ObsoleteAttribute' }],
      }),
    ).toBe('[Obsolete]\npublic void Run()');
  });

  test('uses C# operator names instead of CLR metadata names', () => {
    expect(
      memberDisplayName({
        name: 'op_Equality',
        kind: 'method',
        signature: 'public static bool ChannelId.operator ==(ChannelId left, ChannelId right)',
        parameters: [
          { name: 'left', type: 'Sample.ChannelId' },
          { name: 'right', type: 'Sample.ChannelId' },
        ],
      }),
    ).toBe('operator ==(ChannelId, ChannelId)');
  });

  test('links crefs to types in other generated packages', () => {
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
      renderDocNodes(
        [{ kind: 'cref', value: 'T:Orleans.Runtime.GrainType' }],
        {
          packages: [pkg, targetPackage],
          packageName: pkg.package.name,
          base: '/orleans/',
        },
      ),
    ).toBe(
      '[GrainType](/orleans/docs/api/csharp/microsoft.orleans.core.abstractions/orleans.runtime.graintype/)',
    );
  });
});
