import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fromHtml } from 'hast-util-from-html';
import remarkParse from 'remark-parse';
import { unified } from 'unified';
import { visit } from 'unist-util-visit';
import YAML from 'yaml';
import { describe, expect, test } from 'vitest';
import {
  collectIncludeTargets,
  isSnippetSupportMarkdown,
} from '../scripts/lib/docfx.mjs';

const sourceRoot = path.resolve('src');
const contentRoot = path.join(sourceRoot, 'content');
const documentationRoot = path.join(contentRoot, 'docs');
const repositoryRoot = path.resolve('../..');
const samplesRoot = path.join(repositoryRoot, 'samples');
const learnProductRoots = new Set([
  'aspnet',
  'azure',
  'cli',
  'contribute',
  'core',
  'csharp',
  'devops',
  'dotnet',
  'framework',
  'nuget',
  'previous-versions',
  'shows',
  'sql',
  'standard',
  'visualstudio',
]);
const markdownYamlFields = new Set(['description', 'footer', 'summary']);
const urlYamlFields = new Set(['homepage', 'href', 'url']);
const excludedDirectoryNames = new Set([
  'bin',
  'node_modules',
  'obj',
]);

function filesUnder(directory, extensions) {
  return readdirSync(directory, { recursive: true })
    .filter(
      (file) =>
        extensions.some((extension) => file.endsWith(extension)) &&
        !file
          .split(/[\\/]/)
          .some((segment) => excludedDirectoryNames.has(segment)),
    )
    .map((file) => path.join(directory, file));
}

function localTargetExists(sourceFile, url) {
  let pathname;
  try {
    pathname = decodeURIComponent(url.split(/[?#]/, 1)[0]);
  } catch {
    return false;
  }

  const target = path.resolve(path.dirname(sourceFile), pathname);
  return (
    existsSync(target) ||
    existsSync(`${target.replace(/[\\/]$/, '')}.md`) ||
    existsSync(path.join(target, 'index.md'))
  );
}

function isLearnOwnedRelativeUrl(sourceFile, url) {
  if (
    typeof url !== 'string' ||
    url.length === 0 ||
    url.startsWith('#') ||
    /^[a-z][a-z\d+.-]*:/i.test(url)
  ) {
    return false;
  }
  let pathname;
  try {
    pathname = decodeURIComponent(url.split(/[?#]/, 1)[0]);
  } catch {
    return true;
  }
  const relativeProductPath = pathname
    .replace(/^\/+/, '')
    .replace(/^(?:\.\.?\/)+/, '');
  const productRoot = relativeProductPath.split('/', 1)[0].toLowerCase();
  return (
    learnProductRoots.has(productRoot) &&
    !localTargetExists(sourceFile, url)
  );
}

function collectMarkdownUrls(source, sourceFile) {
  const result = [];
  const tree = unified().use(remarkParse).parse(source);
  visit(tree, ['definition', 'image', 'link'], (node) => {
    result.push({ position: node.position?.start.line, url: node.url });
  });
  function collectHtmlUrls(value, position) {
    const html = fromHtml(value, { fragment: true });
    visit(html, 'element', (element) => {
      const line = position + (element.position?.start.line ?? 1) - 1;
      for (const property of ['href', 'src']) {
        const url = element.properties?.[property];
        if (typeof url === 'string') {
          result.push({ position: line, url });
        }
      }
    });
  }
  visit(tree, 'html', (node) => {
    collectHtmlUrls(node.value, node.position?.start.line);
  });
  visit(tree, 'text', (node) => {
    const start = node.position?.start.offset;
    const end = node.position?.end.offset;
    if (start === undefined || end === undefined) {
      return;
    }
    const rawText = source.slice(start, end);
    for (const match of rawText.matchAll(/<[A-Za-z][^>\r\n]*>/g)) {
      if (match.index > 0 && rawText[match.index - 1] === '\\') {
        continue;
      }
      const preceding = rawText.slice(0, match.index);
      const lineOffset = [...preceding.matchAll(/\r\n|\r|\n/g)].length;
      collectHtmlUrls(match[0], node.position.start.line + lineOffset);
    }
  });
  return result
    .filter(({ url }) => isLearnOwnedRelativeUrl(sourceFile, url))
    .sort((left, right) => left.position - right.position || left.url.localeCompare(right.url));
}

function collectYamlUrls(value, sourceFile, keyPath = []) {
  if (Array.isArray(value)) {
    return value.flatMap((item, index) =>
      collectYamlUrls(item, sourceFile, [...keyPath, index]),
    );
  }
  if (value && typeof value === 'object') {
    return Object.entries(value).flatMap(([key, item]) =>
      collectYamlUrls(item, sourceFile, [...keyPath, key]),
    );
  }
  if (typeof value !== 'string') {
    return [];
  }

  const result = [];
  const key = keyPath.at(-1);
  if (urlYamlFields.has(key) && isLearnOwnedRelativeUrl(sourceFile, value)) {
    result.push({ keyPath: keyPath.join('.'), url: value });
  }
  if (markdownYamlFields.has(key)) {
    for (const item of collectMarkdownUrls(value, sourceFile)) {
      result.push({ keyPath: keyPath.join('.'), url: item.url });
    }
  }
  return result;
}

describe('documentation corpus links', () => {
  test('reports parser-backed URLs once with CRLF source lines', () => {
    const sourceFile = path.join(documentationRoot, 'fixture.md');
    const source = [
      'First line',
      '[First][learn]',
      '[Second][learn]',
      '',
      "[learn]: /azure/example 'Example'",
      '',
      "<A HREF = '/visualstudio/example'>Visual Studio</A>",
      '',
      '<img SRC=/nuget/example>',
      '',
      '[Bare](standard/example)',
      '',
      '\\<a href="/azure/escaped">Escaped HTML</a>',
      '&lt;a href="/azure/encoded"&gt;Encoded HTML&lt;/a&gt;',
      '',
      '<div>',
      '<a href="/devops/example">DevOps</a>',
      '</div>',
      '[App Service sign-in](/.auth/login/aad)',
      '[Application route](/hello/0)',
    ].join('\r\n');

    expect(collectMarkdownUrls(source, sourceFile)).toEqual([
      { position: 5, url: '/azure/example' },
      { position: 7, url: '/visualstudio/example' },
      { position: 9, url: '/nuget/example' },
      { position: 11, url: 'standard/example' },
      { position: 17, url: '/devops/example' },
    ]);
  });

  test('recognizes supported YAML URL and Markdown fields', () => {
    const sourceFile = path.join(documentationRoot, 'fixture.yml');
    const yaml = {
      homepage: '/aspnet/example',
      name: '/azure/plain-label',
      summary: 'See [Azure](/azure/example).',
    };

    expect(collectYamlUrls(yaml, sourceFile)).toEqual([
      { keyPath: 'homepage', url: '/aspnet/example' },
      { keyPath: 'summary', url: '/azure/example' },
    ]);
  });

  test('uses fully qualified URLs for Learn content outside Orleans', async () => {
    const failures = [];
    const documentationFiles = filesUnder(documentationRoot, ['.md']).filter(
      (file) =>
        !isSnippetSupportMarkdown(path.relative(documentationRoot, file)),
    );
    const includeFiles = await collectIncludeTargets(documentationFiles, sourceRoot);
    const auditedMarkdownFiles = new Set([
      ...filesUnder(documentationRoot, ['.md']),
      ...includeFiles,
      ...filesUnder(samplesRoot, ['.md']),
    ]);
    for (const file of [...auditedMarkdownFiles].sort()) {
      for (const item of collectMarkdownUrls(readFileSync(file, 'utf8'), file)) {
        failures.push({
          file: path.relative(repositoryRoot, file).replaceAll('\\', '/'),
          line: item.position,
          url: item.url,
        });
      }
    }
    const yamlFiles = [
      ...filesUnder(documentationRoot, ['.yaml', '.yml']),
      ...filesUnder(samplesRoot, ['.yaml', '.yml']),
    ];
    for (const file of yamlFiles) {
      const documents = YAML.parseAllDocuments(readFileSync(file, 'utf8'));
      for (const document of documents) {
        if (document.errors.length > 0) {
          throw document.errors[0];
        }
        for (const item of collectYamlUrls(document.toJS(), file)) {
          failures.push({
            file: path.relative(repositoryRoot, file).replaceAll('\\', '/'),
            key: item.keyPath,
            url: item.url,
          });
        }
      }
    }

    expect(failures).toEqual([]);
  }, 30_000);
});
