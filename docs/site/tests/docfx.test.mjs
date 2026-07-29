import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  convertDocfxMarkdown,
  convertHubYaml,
  createSidebar,
} from '../scripts/lib/docfx.mjs';

const temporaryDirectories = [];

async function temporaryDirectory() {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-docfx-'));
  temporaryDirectories.push(directory);
  return directory;
}

afterEach(async () => {
  const { rm } = await import('node:fs/promises');
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe('DocFX conversion', () => {
  test('expands includes and source regions while converting supported constructs', async () => {
    const directory = await temporaryDirectory();
    await mkdir(path.join(directory, 'includes'));
    await writeFile(
      path.join(directory, 'includes', 'note.md'),
      [
        '> [!TIP]',
        '> Included guidance.',
        '',
        '![Included image](media/included.png)',
        '',
        '```markdown',
        '[Literal link example](literal.png)',
        '```',
        '',
      ].join('\n'),
    );
    await writeFile(
      path.join(directory, 'Example.cs'),
      [
        'internal class Example',
        '{',
        '    // <hello>',
        '    public string SayHello() => "Hello";',
        '    // </hello>',
        '}',
      ].join('\n'),
    );
    const sourcePath = path.join(directory, 'guide.md');
    const source = [
      '---',
      'title: Test guide',
      'description: Converter fixture.',
      'ms.date: 07/28/2026',
      '---',
      '# Test guide',
      '[!INCLUDE [note](includes/note.md)]',
      '',
      ':::code language="csharp" source="Example.cs" id="hello":::',
      '',
      ':::image type="content" source="image.png" alt-text="An image" lightbox="image.png":::',
      '',
      '<xref:Orleans.IGrain>',
      '',
      '<xref:Orleans.Overview>',
      '',
      '<xref:System.Func`8>',
      '',
      '> [!VIDEO https://aka.ms/docs/player?show=reactor&ep=orleans]',
      '',
      '> [!div class="nextstepaction"]',
      '> [Continue learning](next.md)',
      '',
      '> [!div class="checklist"]',
      '>',
      '> - First task',
      '> - Second task',
      '',
      '[ObserverManager\\<IChat>](<xref:Orleans.Utilities.ObserverManager`1>)',
      '',
      '<xref:Orleans.Utilities.ObserverManager`2.Notify*> method on [ObserverManager\\<IChat>](<xref:Orleans.Utilities.ObserverManager`1>)',
      '',
      '<xref:Orleans.IGrainFactory.GetGrain``1(System.Type,System.Guid)?displayProperty=nameWithType> and [grain references](next.md)',
      '',
      '[`[assembly: GenerateSerializer(Type)]`](xref:Orleans.CodeGeneration.GenerateSerializerAttribute)',
      '',
      '# [Visual Studio](#tab/visual-studio)',
      '',
      '## Named section <a name="named-section"></a>',
      '',
      '[Repository](<https://github.com/dotnet/orleans>)',
      '',
      'Before<hr><input type="text"><source src="sample.mp4">After',
      '',
      '::: zone target="docs" pivot="orleans-9-0,orleans-10-0"',
      'Versioned text.',
      '::: zone-end',
    ].join('\n');

    const converted = await convertDocfxMarkdown({
      source,
      sourcePath,
      sourceRoot: directory,
      uidMap: new Map([['Orleans.Overview', '/orleans/docs/overview/']]),
    });

    expect(converted).toContain(':::tip[Tip]');
    expect(converted).toContain('![Included image](./includes/media/included.png)');
    expect(converted).toContain('[Literal link example](literal.png)');
    expect(converted).not.toContain('[Literal link example](./includes/literal.png)');
    expect(converted).toContain('public string SayHello() => "Hello";');
    expect(converted).not.toContain('// <hello>');
    expect(converted).toContain('{/* Source: Example.cs; region: hello */}');
    expect(converted).toContain('![An image](image.png)');
    expect(converted).toContain(
      '[ObserverManager&lt;IChat>](https://learn.microsoft.com/dotnet/api/orleans.utilities.observermanager-1)',
    );
    expect(converted).toContain(
      '[Notify](https://learn.microsoft.com/dotnet/api/orleans.utilities.observermanager-2.notify) method on [ObserverManager&lt;IChat>](https://learn.microsoft.com/dotnet/api/orleans.utilities.observermanager-1)',
    );
    expect(converted).toContain('[grain references](/orleans/docs/next/)');
    expect(converted).toContain(
      '[`[assembly: GenerateSerializer(Type)]`](https://learn.microsoft.com/dotnet/api/orleans.codegeneration.generateserializerattribute)',
    );
    expect(converted).toContain(
      '[IGrain](https://learn.microsoft.com/dotnet/api/orleans.igrain)',
    );
    expect(converted).toContain('[Overview](/orleans/docs/overview/)');
    expect(converted).toContain('slug: docs/guide');
    expect(converted).toContain('title: Test guide');
    expect(converted).toContain(
      '[Func&lt;T, U, V, W, X, Y, Z, T8&gt;](https://learn.microsoft.com/dotnet/api/system.func-8)',
    );
    expect(converted).toContain('<div class="video-embed">');
    expect(converted).toContain(
      'src="https://aka.ms/docs/player?show=reactor&amp;ep=orleans"',
    );
    expect(converted).toContain(':::tip[Next step]\n[Continue learning](/orleans/docs/next/)');
    expect(converted).toContain('- First task\n- Second task');
    expect(converted).not.toContain('[!VIDEO');
    expect(converted).not.toContain('[!div');
    expect(converted).toContain('### Visual Studio');
    expect(converted).toContain('<span id="named-section"></span>');
    expect(converted).toContain('[Repository](https://github.com/dotnet/orleans)');
    expect(converted).toContain(
      'Before<hr /><input type="text" /><source src="sample.mp4" />After',
    );
    expect(converted).toContain('::::version{versions="Orleans 9.0, Orleans 10.0"}');
    expect(converted).not.toContain('# Test guide');
  });

  test('fails when a code region cannot be found', async () => {
    const directory = await temporaryDirectory();
    await writeFile(path.join(directory, 'Example.cs'), 'internal class Example {}\n');
    const sourcePath = path.join(directory, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source: [
          '---',
          'title: Broken snippet',
          '---',
          ':::code source="Example.cs" id="missing":::',
        ].join('\n'),
        sourcePath,
      }),
    ).rejects.toThrow("Snippet region 'missing' was not found");
  });

  test('fails when an include cannot be found', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    await expect(
      convertDocfxMarkdown({
        source: '---\ntitle: Missing include\n---\n[!INCLUDE [missing](missing.md)]',
        sourcePath,
      }),
    ).rejects.toThrow("INCLUDE 'missing.md'");
  });

  test('converts long escaped xref labels without regex backtracking', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const escapedLabel = '\\\\'.repeat(10_000);

    const converted = await convertDocfxMarkdown({
      source: `---\ntitle: Xrefs\n---\n[${escapedLabel}](xref:Orleans.IGrain)`,
      sourcePath,
    });

    expect(converted).toContain('(https://learn.microsoft.com/dotnet/api/orleans.igrain)');
  });

  test('scans unmatched link labels in linear time', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const unmatchedLabels = '['.repeat(100_000);

    const converted = await convertDocfxMarkdown({
      source: `---\ntitle: Links\n---\n${unmatchedLabels}`,
      sourcePath,
    });

    expect(converted).toContain(unmatchedLabels);
  });

  test('scans unmatched link destinations in linear time', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const unmatchedDestinations = '[]('.repeat(100_000);

    const converted = await convertDocfxMarkdown({
      source: `---\ntitle: Links\n---\n${unmatchedDestinations}`,
      sourcePath,
    });

    expect(converted).toContain(unmatchedDestinations);
  });

  test('converts links after code spans containing backslashes', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');

    const converted = await convertDocfxMarkdown({
      source: '---\ntitle: Links\n---\n`C:\\\\` [Next](next.md)',
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain('`C:\\\\` [Next](/orleans/docs/next/)');
  });

  test('extracts a plain-text title from inline HTML', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');

    const converted = await convertDocfxMarkdown({
      source: '---\ntitle: Fallback\n---\n# <span>Safe title</span>',
      sourcePath,
    });

    expect(converted).toContain('title: Safe title');
    expect(converted).not.toContain('<span>');
  });

  test('builds navigation from toc.yml and rejects missing targets', async () => {
    const directory = await temporaryDirectory();
    await writeFile(path.join(directory, 'overview.md'), '---\ntitle: Overview\n---\n');
    const tocPath = path.join(directory, 'toc.yml');
    await writeFile(
      tocPath,
      'items:\n  - name: Get started\n    items:\n      - name: Overview\n        href: overview.md\n',
    );

    await expect(createSidebar(tocPath)).resolves.toEqual([
      {
        label: 'Get started',
        items: [{ label: 'Overview', link: '/docs/overview/' }],
      },
    ]);

    await writeFile(tocPath, 'items:\n  - name: Missing\n    href: missing.md\n');
    await expect(createSidebar(tocPath)).rejects.toThrow("target 'missing.md'");
  });

  test('converts a YamlMime hub without dropping link groups', async () => {
    const directory = await temporaryDirectory();
    const hubPath = path.join(directory, 'index.yml');
    await writeFile(
      hubPath,
      [
        '### YamlMime:Hub',
        'title: Orleans',
        'summary: Distributed applications.',
        'brand: dotnet',
        'highlightedContent:',
        '  items:',
        '    - title: Overview',
        '      itemType: overview',
        '      url: overview.md',
        'conceptualContent:',
        '  title: Learn',
        '  items:',
        '    - title: Grains',
        '      links:',
        '        - text: Identity',
        '          url: grains/identity.md',
      ].join('\n'),
    );

    const converted = await convertHubYaml(hubPath);
    expect(converted).toContain('template: splash');
    expect(converted).toContain('href="/orleans/docs/overview/"');
    expect(converted).toContain('href="/orleans/docs/grains/identity/"');
    expect(converted).toContain('Identity');
  });
});
