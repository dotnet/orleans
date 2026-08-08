import { mkdtemp, mkdir, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  collectIncludeTargets,
  collectUidMap,
  convertDocfxMarkdown,
  convertHubYaml,
  createSidebar,
  isSnippetSupportMarkdown,
} from '../scripts/lib/docfx.mjs';

const temporaryDirectories = [];

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

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
  test('identifies inactive snippet support Markdown', () => {
    expect(isSnippetSupportMarkdown('host/snippets/README.md')).toBe(true);
    expect(isSnippetSupportMarkdown('host/snippets-v3/ReadMe.md')).toBe(true);
    expect(isSnippetSupportMarkdown('host/README.md')).toBe(false);
    expect(isSnippetSupportMarkdown('host/snippets/guide.md')).toBe(false);
  });

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
    await writeFile(path.join(directory, 'image.png'), '');
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

  test('maps public Orleans xrefs to generated native API routes', async () => {
    const directory = await temporaryDirectory();
    const apiRoot = path.join(directory, 'pkgs');
    await mkdir(apiRoot);
    await writeFile(
      path.join(apiRoot, 'Example.json'),
      JSON.stringify({
        package: { name: 'Microsoft.Orleans.Example' },
        types: [
          {
            name: 'Example',
            fullName: 'Orleans.Example<T>',
            genericParameters: [{ name: 'T' }],
            members: [
              {
                name: 'RunAsync',
                kind: 'method',
                signature: 'public Task Example<T>.RunAsync()',
              },
            ],
          },
          {
            name: 'Mode',
            fullName: 'Orleans.Mode',
            enumMembers: [{ name: 'Active', value: '1' }],
          },
        ],
      }),
    );
    const sourcePath = path.join(directory, 'guide.md');
    await writeFile(sourcePath, '# Guide\n');
    const uidMap = await collectUidMap([sourcePath], directory, apiRoot);
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Native API xrefs',
        '---',
        '<xref:Orleans.Example`1>',
        '<xref:Orleans.Example`1.RunAsync*>',
        '<xref:Orleans.Mode.Active>',
        '<xref:System.String>',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
      uidMap,
    });

    expect(converted).toContain(
      '[Example&lt;T&gt;](/orleans/docs/api/csharp/microsoft.orleans.example/orleans.example-1/)',
    );
    expect(converted).toContain(
      '[RunAsync](/orleans/docs/api/csharp/microsoft.orleans.example/orleans.example-1/methods/)',
    );
    expect(converted).toContain(
      '[Active](/orleans/docs/api/csharp/microsoft.orleans.example/orleans.mode/#fields)',
    );
    expect(converted).toContain(
      '[String](https://learn.microsoft.com/dotnet/api/system.string)',
    );
  });

  test('preserves triple-backtick content inside a four-backtick fence', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Fences',
        '---',
        '````text',
        '```csharp',
        'List<T> values;',
        '<!-- literal code comment -->',
        '```',
        '````',
        'List<T> outside.',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain('```csharp\nList<T> values;\n<!-- literal code comment -->\n```');
    expect(converted).not.toContain('List&lt;T> values;');
    expect(converted).not.toContain('{/* literal code comment */}');
    expect(converted).toContain('List&lt;T> outside.');
  });

  test.each([
    ['tilde fence', '~~~~text', '~~~~'],
    ['longer tilde close', '~~~text', '~~~~~'],
    ['longer backtick close', '```text', '`````'],
  ])('honors %s delimiter semantics during conversion', async (_name, opening, closing) => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Fences',
        '---',
        opening,
        'List<T> inside;',
        closing,
        'List<T> outside.',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain('List<T> inside;');
    expect(converted).toContain('List&lt;T> outside.');
  });

  test.each(['\n', '\r\n'])(
    'converts only active include and code directives using %j',
    async (newline) => {
      const directory = await temporaryDirectory();
      await writeFile(path.join(directory, 'active.md'), 'Active include.\n');
      await writeFile(path.join(directory, 'Active.cs'), 'internal class Active {}\n');
      const sourcePath = path.join(directory, 'guide.md');
      const literalDirectives = [
        '````markdown',
        '```text',
        ':::code language="csharp" source="Missing.cs":::',
        '[!INCLUDE [missing](missing.md)]',
        '```',
        '````',
        '~~~text',
        ':::code language="csharp" source="Missing.cs":::',
        '[!INCLUDE [missing](missing.md)]',
        '~~~',
        '1. Literal example:',
        '',
        '   ```text',
        '   :::code language="csharp" source="Missing.cs":::',
        '   [!INCLUDE [missing](missing.md)]',
        '   ```',
        '       :::code language="csharp" source="Missing.cs":::',
        '> :::code language="csharp" source="Missing.cs":::',
        '> [!INCLUDE [missing](missing.md)]',
        'Use `:::code language="csharp" source="Missing.cs":::` literally.',
        'Use `[!INCLUDE [missing](missing.md)]` literally.',
        '<pre>',
        ':::code language="csharp" source="Missing.cs":::',
        '[!INCLUDE [missing](missing.md)]',
        '</pre>',
      ];
      const converted = await convertDocfxMarkdown({
        source: [
          '---',
          'title: Literal directives',
          '---',
          ...literalDirectives,
          '[!INCLUDE [active](active.md)]',
          ':::code language="csharp" source="Active.cs":::',
        ].join(newline),
        sourcePath,
        sourceRoot: directory,
      });

      expect(converted).toContain('Active include.');
      expect(converted).toContain('internal class Active {}');
      expect(converted).toContain('source="Missing.cs"');
      expect(converted).toContain('[!INCLUDE [missing](missing.md)]');
    },
  );

  test.each(['\n', '\r\n'])(
    'preserves literal directives while converting direct and lazy callouts using %j',
    async (newline) => {
      const directory = await temporaryDirectory();
      await writeFile(path.join(directory, 'active.md'), 'Active include.\n');
      await writeFile(path.join(directory, 'Active.cs'), 'internal class Active {}\n');
      const sourcePath = path.join(directory, 'guide.md');
      const converted = await convertDocfxMarkdown({
        source: [
          '---',
          'title: Callout literals',
          '---',
          '````markdown',
          '> [!NOTE]',
          '> Literal callout example.',
          '````',
          '<pre>',
          '> [!div class="checklist"]',
          '> Literal Learn block example.',
          '</pre>',
          '> [!NOTE]',
          '> [!INCLUDE [missing](missing.md)]',
          '> :::code language="csharp" source="Missing.cs":::',
          '',
          '> [!TIP]',
          '> Literal directives:',
          '[!INCLUDE [missing](missing.md)]',
          ':::code language="csharp" source="Missing.cs":::',
          '',
          '> [!IMPORTANT]',
          '> 1. Nested literal directives:',
          '>    [!INCLUDE [missing](missing.md)]',
          '>    :::code language="csharp" source="Missing.cs":::',
          '',
          '> [!WARNING]',
          '> ````text',
          '> ```text',
          '> [!INCLUDE [missing](missing.md)]',
          '> :::code language="csharp" source="Missing.cs":::',
          '> ```',
          '> ````',
          '',
          '> [!div class="checklist"]',
          '> [!INCLUDE [missing](missing.md)]',
          '> :::code language="csharp" source="Missing.cs":::',
          '',
          '[!INCLUDE [active](active.md)]',
          ':::code language="csharp" source="Active.cs":::',
        ].join(newline),
        sourcePath,
        sourceRoot: directory,
      });

      expect(converted).toContain(':::note[Note]\n&#91;!INCLUDE');
      expect(converted).toContain('````markdown\n> [!NOTE]\n> Literal callout example.\n````');
      expect(converted).toContain(
        '<pre>\n> [!div class="checklist"]\n> Literal Learn block example.\n</pre>',
      );
      expect(converted).toContain(':::tip[Tip]\nLiteral directives:\n&#91;!INCLUDE');
      expect(converted).toContain('&#58;&#58;&#58;code language="csharp" source="Missing.cs":::');
      expect(converted).toContain('````text\n```text\n[!INCLUDE [missing](missing.md)]');
      expect(converted).toContain('Active include.');
      expect(converted).toContain('internal class Active {}');
    },
  );

  test('preserves container-relative indentation for lazy callout lines', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Lazy indentation',
        '---',
        '   > [!NOTE]',
        ' lazy body',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(':::note[Note]\n   lazy body\n   :::');
    expect(converted).not.toContain('\n    lazy body');
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

  test('rejects nested version zones which cannot be rendered safely', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source: [
          '---',
          'title: Nested zones',
          '---',
          ':::zone target="docs" pivot="orleans-9-0"',
          'Outer content.',
          ':::zone target="docs" pivot="orleans-8-0"',
          'Inner content.',
          ':::zone-end',
          ':::zone-end',
        ].join('\n'),
        sourcePath,
        sourceRoot: directory,
      }),
    ).rejects.toThrow(`Nested version zones are not supported in ${sourcePath}.`);
  });

  test('preserves literal zone directives inside a real version zone', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Literal zones',
        '---',
        ':::zone target="docs" pivot="orleans-9-0"',
        '```text',
        ':::zone target="docs" pivot="orleans-8-0"',
        ':::zone-end',
        '```',
        'Historical content.',
        ':::zone-end',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain('::::version{versions="Orleans 9.0"}');
    expect(converted).toContain(
      '```text\n:::zone target="docs" pivot="orleans-8-0"\n:::zone-end\n```',
    );
  });

  test.each(['\n', '\r\n'])(
    'preserves a fake zone opening in list-contained indented code using %j',
    async (newline) => {
      const directory = await temporaryDirectory();
      const sourcePath = path.join(directory, 'guide.md');
      const converted = await convertDocfxMarkdown({
        source: [
          '---',
          'title: Indented example',
          '---',
          '- Example:',
          '',
          '      :::zone target="docs" pivot="orleans-9-0"',
          '',
          'Orleans 9 is current prose.',
        ].join(newline),
        sourcePath,
        sourceRoot: directory,
      });

      expect(converted).toContain(
        '- Example:\n\n      :::zone target="docs" pivot="orleans-9-0"',
      );
      expect(converted).not.toContain('::::version');
    },
  );

  test('preserves fake zone directives in list-contained fenced code', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Fenced example',
        '---',
        '- Example:',
        '',
        '  ```text',
        '  :::zone target="docs" pivot="orleans-9-0"',
        '  :::zone-end',
        '  ```',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(
      '  ```text\n  :::zone target="docs" pivot="orleans-9-0"\n  :::zone-end\n  ```',
    );
    expect(converted).not.toContain('::::version');
  });

  test('preserves fake zone directives in blockquote and HTML examples', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Container examples',
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
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(
      '> ```text\n> :::zone target="docs" pivot="orleans-9-0"\n> :::zone-end\n> ```',
    );
    expect(converted).toContain(
      '<pre>\n:::zone target="docs" pivot="orleans-8-0"\n:::zone-end\n</pre>',
    );
    expect(converted).not.toContain('::::version');
  });

  test('does not transform fake zone directives exposed by blockquote conversion', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Blockquote examples',
        '---',
        '> [!NOTE]',
        '> :::zone target="docs" pivot="orleans-9-0"',
        '> :::zone-end',
        '',
        '> [!div class="checklist"]',
        '> :::zone target="docs" pivot="orleans-8-0"',
        '> :::zone-end',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(
      ':::note[Note]\n:::zone target="docs" pivot="orleans-9-0"\n:::zone-end\n:::',
    );
    expect(converted).toContain(
      ':::zone target="docs" pivot="orleans-8-0"\n:::zone-end',
    );
    expect(converted).not.toContain('::::version');
  });

  test('preserves zone text in inline code', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Inline example',
        '---',
        'Use `:::zone target="docs" pivot="orleans-9-0"` as literal text.',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(
      'Use `:::zone target="docs" pivot="orleans-9-0"` as literal text.',
    );
    expect(converted).not.toContain('::::version');
  });

  test('does not close a real zone from list-contained indented code', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: Literal close',
        '---',
        ':::zone target="docs" pivot="orleans-9-0"',
        '- Example:',
        '',
        '      :::zone-end',
        '',
        'Historical content.',
        ':::zone-end',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain('::::version{versions="Orleans 9.0"}');
    expect(converted).toContain('- Example:\n\n      :::zone-end\n\nHistorical content.\n::::');
  });

  test('converts a genuine zone in list prose', async () => {
    const directory = await temporaryDirectory();
    const sourcePath = path.join(directory, 'guide.md');
    const converted = await convertDocfxMarkdown({
      source: [
        '---',
        'title: List zone',
        '---',
        '- Compatibility:',
        '',
        '  :::zone target="docs" pivot="orleans-9-0"',
        '  Historical content.',
        '  :::zone-end',
      ].join('\n'),
      sourcePath,
      sourceRoot: directory,
    });

    expect(converted).toContain(
      '  ::::version{versions="Orleans 9.0"}\n  Historical content.\n  ::::',
    );
  });

  test('allows a relative code source within the explicit snippet boundary', async () => {
    const directory = await temporaryDirectory();
    const docsRoot = path.join(directory, 'docs');
    const snippetsRoot = path.join(docsRoot, 'snippets');
    await mkdir(snippetsRoot, { recursive: true });
    await writeFile(path.join(snippetsRoot, 'settings.json'), '{"safe":true}\n');
    const sourcePath = path.join(docsRoot, 'guide.md');

    const converted = await convertDocfxMarkdown({
      source:
        '---\ntitle: Safe snippet\n---\n:::code language="json" source="snippets/settings.json":::',
      sourcePath,
      sourceRoot: docsRoot,
      snippetRoots: [docsRoot],
    });

    expect(converted).toContain('```json\n{"safe":true}\n```');
    expect(converted).toContain('{/* Source: snippets/settings.json */}');
  });

  test('rejects a traversing code source with its directive line and allowed boundary', async () => {
    const directory = await temporaryDirectory();
    const docsRoot = path.join(directory, 'docs');
    await mkdir(docsRoot);
    await writeFile(path.join(directory, 'global.json'), '{"sdk":{}}\n');
    const sourcePath = path.join(docsRoot, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source:
          '---\ntitle: Traversal\n---\nIntro.\n:::code language="json" source="../global.json":::',
        sourcePath,
        sourceRoot: docsRoot,
        snippetRoots: [docsRoot],
      }),
    ).rejects.toThrow(
      new RegExp(
        `${escapeRegExp(sourcePath)}:5.*resolves outside allowed snippet root\\(s\\).*${escapeRegExp(docsRoot)}`,
      ),
    );
  });

  test('rejects an absolute code source with its directive line and allowed boundary', async () => {
    const directory = await temporaryDirectory();
    const docsRoot = path.join(directory, 'docs');
    await mkdir(docsRoot);
    const outsidePath = path.join(directory, 'runner.json');
    await writeFile(outsidePath, '{"secret":true}\n');
    const sourcePath = path.join(docsRoot, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source: `---\ntitle: Absolute\n---\n:::code language="json" source="${outsidePath}":::`,
        sourcePath,
        sourceRoot: docsRoot,
        snippetRoots: [docsRoot],
      }),
    ).rejects.toThrow(
      new RegExp(
        `${escapeRegExp(sourcePath)}:4.*must be relative.*allowed snippet root\\(s\\).*${escapeRegExp(docsRoot)}`,
      ),
    );
  });

  test('rejects a code source which escapes through a directory link', async ({ skip }) => {
    const directory = await temporaryDirectory();
    const docsRoot = path.join(directory, 'docs');
    const snippetsRoot = path.join(docsRoot, 'snippets');
    const outsideRoot = path.join(directory, 'runner');
    await mkdir(snippetsRoot, { recursive: true });
    await mkdir(outsideRoot);
    await writeFile(path.join(outsideRoot, 'secret.json'), '{"secret":true}\n');
    try {
      await symlink(
        outsideRoot,
        path.join(snippetsRoot, 'escape'),
        process.platform === 'win32' ? 'junction' : 'dir',
      );
    } catch (error) {
      if (['EACCES', 'ENOSYS', 'EPERM'].includes(error.code)) {
        skip();
        return;
      }
      throw error;
    }
    const sourcePath = path.join(docsRoot, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source:
          '---\ntitle: Linked escape\n---\n:::code language="json" source="snippets/escape/secret.json":::',
        sourcePath,
        sourceRoot: docsRoot,
        snippetRoots: [docsRoot],
      }),
    ).rejects.toThrow(
      new RegExp(
        `${escapeRegExp(sourcePath)}:4.*through a link outside allowed snippet root\\(s\\).*${escapeRegExp(docsRoot)}`,
      ),
    );
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

  test('collects the active nested include closure once', async () => {
    const directory = await temporaryDirectory();
    const contentRoot = path.join(directory, 'content');
    const docsRoot = path.join(contentRoot, 'docs');
    const includesRoot = path.join(contentRoot, 'includes');
    await mkdir(docsRoot, { recursive: true });
    await mkdir(includesRoot, { recursive: true });
    const firstPage = path.join(docsRoot, 'first.md');
    const secondPage = path.join(docsRoot, 'second.md');
    const shared = path.join(includesRoot, 'shared.md');
    const nested = path.join(includesRoot, 'nested.md');
    await writeFile(firstPage, '[!INCLUDE [shared](../includes/shared.md)]\r\n');
    await writeFile(secondPage, '[!INCLUDE [shared](../includes/shared.md)]\n');
    await writeFile(shared, '[!INCLUDE [nested](nested.md)]\r\n');
    await writeFile(nested, 'Active nested guidance.\n');
    await writeFile(path.join(includesRoot, 'inactive.md'), 'Inactive guidance.\n');

    const targets = await collectIncludeTargets([firstPage, secondPage], contentRoot);

    expect([...targets].sort()).toEqual([nested, shared].sort());
  });

  test('rejects circular include graphs', async () => {
    const directory = await temporaryDirectory();
    const first = path.join(directory, 'first.md');
    const second = path.join(directory, 'second.md');
    await writeFile(first, '[!INCLUDE [second](second.md)]\n');
    await writeFile(second, '[!INCLUDE [first](first.md)]\n');

    await expect(collectIncludeTargets([first], directory)).rejects.toThrow(
      'Circular INCLUDE detected',
    );
  });

  test('rejects include traversal and absolute paths', async () => {
    const directory = await temporaryDirectory();
    const contentRoot = path.join(directory, 'content');
    const docsRoot = path.join(contentRoot, 'docs');
    await mkdir(docsRoot, { recursive: true });
    const outside = path.join(directory, 'outside.md');
    await writeFile(outside, 'Outside guidance.\n');
    const traversal = path.join(docsRoot, 'traversal.md');
    await writeFile(traversal, '[!INCLUDE [outside](../../outside.md)]\n');
    await expect(collectIncludeTargets([traversal], contentRoot)).rejects.toThrow(
      'resolves outside',
    );

    const absolute = path.join(docsRoot, 'absolute.md');
    await writeFile(absolute, `[!INCLUDE [outside](${outside.replaceAll('\\', '/')})]\n`);
    await expect(collectIncludeTargets([absolute], contentRoot)).rejects.toThrow(
      'Unsafe INCLUDE path',
    );

    const driveRelative = path.join(docsRoot, 'drive-relative.md');
    await writeFile(driveRelative, '[!INCLUDE [outside](nested/C:../outside.md)]\n');
    await expect(collectIncludeTargets([driveRelative], contentRoot)).rejects.toThrow(
      'Unsafe INCLUDE path',
    );
  });

  test('rejects include symlinks which escape the content root', async () => {
    const directory = await temporaryDirectory();
    const contentRoot = path.join(directory, 'content');
    const docsRoot = path.join(contentRoot, 'docs');
    const includesRoot = path.join(contentRoot, 'includes');
    const outsideRoot = path.join(directory, 'outside');
    await mkdir(docsRoot, { recursive: true });
    await mkdir(includesRoot, { recursive: true });
    await mkdir(outsideRoot, { recursive: true });
    await writeFile(path.join(outsideRoot, 'note.md'), 'Outside guidance.\n');
    await symlink(outsideRoot, path.join(includesRoot, 'linked'), 'junction');
    const page = path.join(docsRoot, 'page.md');
    await writeFile(page, '[!INCLUDE [outside](../includes/linked/note.md)]\n');

    await expect(collectIncludeTargets([page], contentRoot)).rejects.toThrow(
      'resolves outside',
    );
  });

  test('rejects image directives whose path casing does not match', async () => {
    const directory = await temporaryDirectory();
    await mkdir(path.join(directory, 'media'));
    await writeFile(path.join(directory, 'media', 'image.png'), '');
    const sourcePath = path.join(directory, 'guide.md');

    await expect(
      convertDocfxMarkdown({
        source:
          '---\ntitle: Image\n---\n:::image type="content" source="media/Image.png" alt-text="Image":::',
        sourcePath,
      }),
    ).rejects.toThrow("does not exist with that exact path");
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
      'items:\n  - name: Get started\n    items:\n      - name: Overview\n        href: overview.md\n  - name: Samples\n    href: /samples/\n',
    );

    await expect(createSidebar(tocPath)).resolves.toEqual([
      {
        label: 'Get started',
        items: [{ label: 'Overview', link: '/docs/overview/' }],
      },
      {
        label: 'Samples',
        link: '/samples/',
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
