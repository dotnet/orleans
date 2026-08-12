import { execFile } from 'node:child_process';
import { copyFile, mkdtemp, mkdir, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { afterEach, describe, expect, test } from 'vitest';

const execFileAsync = promisify(execFile);
const temporaryDirectories = [];
const validator = path.resolve('src/content/docs/validate-snippets.ps1');
const renderedMarkdownResolver = path.resolve('scripts/resolve-rendered-markdown.mjs');
const includeClosureResolver = path.resolve('scripts/lib/include-closure.mjs');
const markdownRangesResolver = path.resolve('scripts/lib/markdown-ranges.mjs');

async function temporaryDirectory() {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-snippet-policy-'));
  temporaryDirectories.push(directory);
  await mkdir(path.join(directory, 'snippets', 'example'), { recursive: true });
  return directory;
}

async function runPolicy(root, siteRoot, projectPolicyPath) {
  const commandArguments = [validator, '-PolicyOnly', '-RootPath', root];
  if (siteRoot) {
    commandArguments.push('-SiteRootPath', siteRoot);
  }
  if (projectPolicyPath) {
    commandArguments.push('-ProjectPolicyPath', projectPolicyPath);
  }
  try {
    const result = await execFileAsync(
      'pwsh',
      commandArguments,
      { env: { ...process.env, DOTNET_NOLOGO: 'true' } },
    );
    return { exitCode: 0, output: `${result.stdout}${result.stderr}` };
  } catch (error) {
    return {
      exitCode: error.code,
      output: `${error.stdout ?? ''}${error.stderr ?? ''}`,
    };
  }
}

async function writeValidatedProject(root, projectBody) {
  await writeFile(
    path.join(root, 'Directory.Build.props'),
    '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
  );
  await writeFile(
    path.join(root, 'snippets', 'example', 'example.csproj'),
    projectBody ??
      '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Orleans.Server" Version="10.2.2" /></ItemGroup></Project>',
  );
}

afterEach(async () => {
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe('snippet project policy', { timeout: 30_000 }, () => {
  test('executes the rendered Markdown resolver without npm packages installed', async () => {
    const root = await temporaryDirectory();
    const toolRoot = path.join(root, 'tool');
    const sourceRoot = path.join(root, 'content');
    await mkdir(path.join(toolRoot, 'lib'), { recursive: true });
    await mkdir(path.join(sourceRoot, 'includes'), { recursive: true });
    await copyFile(renderedMarkdownResolver, path.join(toolRoot, 'resolve-rendered-markdown.mjs'));
    await copyFile(includeClosureResolver, path.join(toolRoot, 'lib', 'include-closure.mjs'));
    await copyFile(markdownRangesResolver, path.join(toolRoot, 'lib', 'markdown-ranges.mjs'));
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      [
        '[!INCLUDE [example](includes/example.md)]',
        '````markdown',
        '```text',
        '[!INCLUDE [missing](includes/missing.md)]',
        ':::code language="csharp" source="Missing.cs":::',
        '```',
        '````',
        '~~~text',
        '[!INCLUDE [missing](includes/missing.md)]',
        ':::code language="csharp" source="Missing.cs":::',
        '~~~',
        '1. Literal example:',
        '',
        '   ```text',
        '   [!INCLUDE [missing](includes/missing.md)]',
        '   :::code language="csharp" source="Missing.cs":::',
        '   ```',
        '       :::code language="csharp" source="Missing.cs":::',
        '> [!INCLUDE [missing](includes/missing.md)]',
        '> :::code language="csharp" source="Missing.cs":::',
        'Use `[!INCLUDE [missing](includes/missing.md)]` literally.',
        'Use `:::code language="csharp" source="Missing.cs":::` literally.',
        '<pre>',
        '[!INCLUDE [missing](includes/missing.md)]',
        ':::code language="csharp" source="Missing.cs":::',
        '</pre>',
        ':::code language="csharp" source="Active.cs":::',
        '',
      ].join('\n'),
    );
    await writeFile(path.join(sourceRoot, 'includes', 'example.md'), 'Included.\n');

    const result = await execFileAsync(
      process.execPath,
      [path.join(toolRoot, 'resolve-rendered-markdown.mjs'), sourceRoot, root],
      { cwd: root, env: { ...process.env, NODE_PATH: '' } },
    );

    expect(JSON.parse(result.stdout)).toEqual([
      { path: path.join(sourceRoot, 'guide.md'), protectedLineRanges: [[2, 11], [14, 26]] },
      { path: path.join(sourceRoot, 'includes', 'example.md'), protectedLineRanges: [] },
    ].sort((left, right) => left.path.localeCompare(right.path)));
  });

  test('evaluates inherited target framework and imported PackageReference updates', async () => {
    const root = await temporaryDirectory();
    await writeFile(
      path.join(root, 'Directory.Build.props'),
      '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
    );
    await writeFile(
      path.join(root, 'snippets', 'example', 'Versions.props'),
      '<Project><ItemGroup><PackageReference Update="Microsoft.Orleans.Server" Version="10.2.2" Condition="\'$(TargetFramework)\' == \'net10.0\'" /></ItemGroup></Project>',
    );
    await writeFile(
      path.join(root, 'snippets', 'example', 'example.csproj'),
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <ItemGroup><PackageReference Include="Microsoft.Orleans.Server" /></ItemGroup>',
        '  <Import Project="Versions.props" />',
        '</Project>',
      ].join('\n'),
    );

    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test('evaluates central PackageVersion metadata', async () => {
    const root = await temporaryDirectory();
    await writeFile(
      path.join(root, 'Directory.Build.props'),
      '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
    );
    await writeFile(
      path.join(root, 'Directory.Packages.props'),
      [
        '<Project>',
        '  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>',
        '  <ItemGroup><PackageVersion Include="Microsoft.Orleans.Server" Version="10.2.2" /></ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    await writeFile(
      path.join(root, 'snippets', 'example', 'example.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Orleans.Server" /></ItemGroup></Project>',
    );

    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test('rejects a non-current VersionOverride', async () => {
    const root = await temporaryDirectory();
    await writeFile(
      path.join(root, 'Directory.Build.props'),
      '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
    );
    await writeFile(
      path.join(root, 'Directory.Packages.props'),
      [
        '<Project>',
        '  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>',
        '  <ItemGroup><PackageVersion Include="Microsoft.Orleans.Server" Version="10.5.0" /></ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    await writeFile(
      path.join(root, 'snippets', 'example', 'example.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Orleans.Server" VersionOverride="9.2.0" /></ItemGroup></Project>',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain('[SNIPPET002]');
    expect(result.output).toContain("evaluates to version '9.2.0'");
  });

  test('does not use PackageVersion items when central management is disabled', async () => {
    const root = await temporaryDirectory();
    await writeFile(
      path.join(root, 'Directory.Build.props'),
      '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
    );
    await writeFile(
      path.join(root, 'Directory.Packages.props'),
      [
        '<Project>',
        '  <PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>',
        '  <ItemGroup><PackageVersion Include="Microsoft.Orleans.Server" Version="10.5.0" /></ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    await writeFile(
      path.join(root, 'snippets', 'example', 'example.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Orleans.Server" /></ItemGroup></Project>',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain('[SNIPPET002]');
    expect(result.output).toContain("evaluates to version '(missing)'");
  });

  test('requires exact 10.2.2 unless a migration project documents an exception', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(
      root,
      '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Orleans.Server" Version="10.2.1" /></ItemGroup></Project>',
    );
    const drift = await runPolicy(root);
    expect(drift.exitCode).toBe(1);
    expect(drift.output).toContain('[SNIPPET002]');
    expect(drift.output).toContain("evaluates to version '10.2.1'");

    await rm(path.join(root, 'snippets'), { recursive: true, force: true });
    const migrationProject = path.join(
      root,
      'migration',
      'snippets',
      'example',
      'example.csproj',
    );
    await mkdir(path.dirname(migrationProject), { recursive: true });
    await writeFile(
      migrationProject,
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <PropertyGroup>',
        '    <OrleansDocumentationVersionException>Compiles the older side of a migration example.</OrleansDocumentationVersionException>',
        '  </PropertyGroup>',
        '  <ItemGroup>',
        '    <PackageReference Include="Microsoft.Orleans.Server" Version="10.2.1" />',
        '  </ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test('accepts a C# directive target contained by a validated project default glob', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(root);
    await writeFile(path.join(root, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(
      path.join(root, 'guide.md'),
      ':::code language="csharp" source="snippets/example/Program.cs" id="program":::\n',
    );

    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test.each(['\n', '\r\n'])(
    'ignores code and include directives in Markdown literal ranges using %j',
    async (newline) => {
      const root = await temporaryDirectory();
      await writeValidatedProject(root);
      await writeFile(path.join(root, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
      await mkdir(path.join(root, 'includes'), { recursive: true });
      await writeFile(path.join(root, 'includes', 'active.md'), 'Active include.\n');
      await writeFile(
        path.join(root, 'guide.md'),
        [
          '````markdown',
          '```text',
          ':::code language="csharp" source="Missing.cs":::',
          '[!INCLUDE [missing](includes/missing.md)]',
          '```',
          '````',
          '~~~text',
          ':::code language="csharp" source="Missing.cs":::',
          '[!INCLUDE [missing](includes/missing.md)]',
          '~~~',
          '1. Literal example:',
          '',
          '   ```text',
          '   :::code language="csharp" source="Missing.cs":::',
          '   [!INCLUDE [missing](includes/missing.md)]',
          '   ```',
          '       :::code language="csharp" source="Missing.cs":::',
          '> :::code language="csharp" source="Missing.cs":::',
          '> [!INCLUDE [missing](includes/missing.md)]',
          'Use `:::code language="csharp" source="Missing.cs":::` literally.',
          'Use `[!INCLUDE [missing](includes/missing.md)]` literally.',
          '<pre>',
          ':::code language="csharp" source="Missing.cs":::',
          '[!INCLUDE [missing](includes/missing.md)]',
          '</pre>',
          '> [!NOTE]',
          '> [!INCLUDE [missing](includes/missing.md)]',
          '> :::code language="csharp" source="Missing.cs":::',
          '',
          '> [!TIP]',
          '> Literal directives:',
          '[!INCLUDE [missing](includes/missing.md)]',
          ':::code language="csharp" source="Missing.cs":::',
          '',
          '[!INCLUDE [active](includes/active.md)]',
          ':::code language="csharp" source="snippets/example/Program.cs" id="program":::',
          '',
        ].join(newline),
      );

      expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
    },
  );

  test('accepts a linked C# Compile item in a validated project', async () => {
    const root = await temporaryDirectory();
    await mkdir(path.join(root, 'shared'));
    await writeFile(path.join(root, 'shared', 'Linked.cs'), 'class Linked {}\n');
    await writeValidatedProject(
      root,
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <ItemGroup>',
        '    <PackageReference Include="Microsoft.Orleans.Server" Version="10.2.2" />',
        '    <Compile Include="../../shared/Linked.cs" Link="Linked.cs" />',
        '  </ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    await writeFile(
      path.join(root, 'guide.md'),
      ':::code language="csharp" source="shared/Linked.cs" id="linked":::\n',
    );

    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test('requires an id when a C# source contains named regions', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(
      root,
      [
        '<Project Sdk="Microsoft.NET.Sdk">',
        '  <ItemGroup>',
        '    <PackageReference Include="Microsoft.Orleans.Server" Version="10.2.2" />',
        '    <Compile Include="../compiled/Program.cs" Link="Program.cs" />',
        '  </ItemGroup>',
        '</Project>',
      ].join('\n'),
    );
    await mkdir(path.join(root, 'snippets', 'compiled'));
    await writeFile(
      path.join(root, 'snippets', 'compiled', 'Program.cs'),
      [
        'class HiddenScaffolding {}',
        '// <displayed>',
        'class Displayed {}',
        '// </displayed>',
      ].join('\n'),
    );
    await writeFile(
      path.join(root, 'guide.md'),
      ':::code language="csharp" source="snippets/compiled/Program.cs":::\n',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      'does not specify an id',
    );
  });

  test('rejects a standalone C# directive target outside validated projects', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(root);
    await writeFile(path.join(root, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(path.join(root, 'Standalone.cs'), 'class Standalone {}\n');
    await writeFile(
      path.join(root, 'guide.md'),
      'First line.\n:::code language="csharp" source="Standalone.cs":::\n',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      'guide.md:2 [SNIPPET003] C# code directive target',
    );
    expect(result.output).toContain('is not an evaluated Compile item');
  });

  test('normalizes C# language whitespace before enforcing ownership', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(root);
    await writeFile(path.join(root, 'Standalone.txt'), 'class Standalone {}\n');
    await writeFile(
      path.join(root, 'guide.md'),
      ':::code language=" csharp " source="Standalone.txt":::\n',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain("target 'Standalone.txt' is not an evaluated Compile item");
  });

  test('rejects a missing code directive target at the directive source line', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(root);
    await writeFile(path.join(root, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(
      path.join(root, 'guide.md'),
      'First line.\nSecond line.\n:::code language="csharp" source="Missing.cs":::\n',
    );

    const result = await runPolicy(root);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      "guide.md:3 [SNIPPET003] Code directive target 'Missing.cs' does not exist",
    );
  });

  test('allows an existing non-C# directive target outside Compile items', async () => {
    const root = await temporaryDirectory();
    await writeValidatedProject(root);
    await writeFile(path.join(root, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(path.join(root, 'settings.json'), '{}\n');
    await writeFile(
      path.join(root, 'guide.md'),
      ':::code language="json" source="settings.json":::\n',
    );

    expect(await runPolicy(root)).toMatchObject({ exitCode: 0 });
  });

  test('rejects traversal to a repository file with the directive line and allowed boundary', async () => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'docs');
    await mkdir(path.join(sourceRoot, 'snippets', 'example'), { recursive: true });
    await writeValidatedProject(sourceRoot);
    await writeFile(path.join(sourceRoot, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(path.join(siteRoot, 'global.json'), '{"sdk":{}}\n');
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      'First line.\n:::code language="json" source="../global.json":::\n',
    );

    const result = await runPolicy(sourceRoot, siteRoot);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      `${path.join('docs', 'guide.md')}:2 [SNIPPET003] Code directive target '../global.json' resolves outside allowed snippet root`,
    );
    expect(result.output).toContain(sourceRoot);
  });

  test('rejects an absolute target with the directive line and allowed boundary', async () => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'docs');
    await mkdir(path.join(sourceRoot, 'snippets', 'example'), { recursive: true });
    await writeValidatedProject(sourceRoot);
    await writeFile(path.join(sourceRoot, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    const runnerFile = path.join(siteRoot, 'runner.json');
    await writeFile(runnerFile, '{"secret":true}\n');
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      `:::code language="json" source="${runnerFile}":::\n`,
    );

    const result = await runPolicy(sourceRoot, siteRoot);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      `${path.join('docs', 'guide.md')}:1 [SNIPPET003] Code directive target '${runnerFile}' must be relative`,
    );
    expect(result.output).toContain(`allowed snippet root '${sourceRoot}'`);
  });

  test('rejects a target which escapes the allowed boundary through a directory link', async ({
    skip,
  }) => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'docs');
    const snippetsRoot = path.join(sourceRoot, 'snippets', 'example');
    const runnerRoot = path.join(siteRoot, 'runner');
    await mkdir(snippetsRoot, { recursive: true });
    await mkdir(runnerRoot);
    await writeValidatedProject(sourceRoot);
    await writeFile(path.join(snippetsRoot, 'Program.cs'), 'class Program {}\n');
    await writeFile(path.join(runnerRoot, 'secret.json'), '{"secret":true}\n');
    try {
      await symlink(
        runnerRoot,
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
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      ':::code language="json" source="snippets/example/escape/secret.json":::\n',
    );

    const result = await runPolicy(sourceRoot, siteRoot);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      `${path.join('docs', 'guide.md')}:1 [SNIPPET003] Code directive target 'snippets/example/escape/secret.json' resolves through a link outside allowed snippet root`,
    );
    expect(result.output).toContain(sourceRoot);
  });

  test('rejects standalone C# directives in included Markdown outside the source root', async () => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
    await mkdir(path.join(sourceRoot, 'snippets', 'example'), { recursive: true });
    await writeValidatedProject(sourceRoot);
    await writeFile(path.join(sourceRoot, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      '[!INCLUDE [external](../../../external.markdown)]\n',
    );
    await writeFile(
      path.join(siteRoot, 'external.markdown'),
      ':::code language="csharp" source="Standalone.cs":::\n',
    );
    await writeFile(path.join(siteRoot, 'Standalone.cs'), 'class Standalone {}\n');

    const result = await runPolicy(sourceRoot, siteRoot);
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain(
      'external.markdown:1 [SNIPPET003] Code directive target',
    );
    expect(result.output).toContain('resolves outside allowed snippet root');
  });

  test('accepts project-owned C# directives in included Markdown outside the source root', async () => {
    const siteRoot = await temporaryDirectory();
    const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
    await mkdir(path.join(sourceRoot, 'snippets', 'example'), { recursive: true });
    await writeValidatedProject(sourceRoot);
    await writeFile(path.join(sourceRoot, 'snippets', 'example', 'Program.cs'), 'class Program {}\n');
    await writeFile(
      path.join(sourceRoot, 'guide.md'),
      '[!INCLUDE [external](../../../external.md)]\n',
    );
    await writeFile(
      path.join(siteRoot, 'external.md'),
      ':::code language="csharp" source="src/content/docs/snippets/example/Program.cs" id="program":::\n',
    );

    expect(await runPolicy(sourceRoot, siteRoot)).toMatchObject({ exitCode: 0 });
  });
});
