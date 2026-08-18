import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import legacyPaths from '../src/data/legacy-pages.json' with { type: 'json' };
import redirects from '../src/data/redirects.json' with { type: 'json' };
import { compatibilityOutputPath } from './lib/compatibility-paths.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distRoot = path.join(siteRoot, 'dist');
const maxPublishedBytes = 1024 * 1024 * 1024;
const maxApiRootBytes = 1024 * 1024;
const failures = [];

async function walk(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(entryPath)));
    } else if (entry.isFile()) {
      files.push(entryPath);
    }
  }
  return files;
}

function relative(file) {
  return path.relative(distRoot, file).replaceAll('\\', '/');
}

function fail(file, message) {
  failures.push(`${relative(file)}: ${message}`);
}

const files = await walk(distRoot);
const totalBytes = (await Promise.all(files.map(async (file) => (await stat(file)).size))).reduce(
  (total, size) => total + size,
  0,
);

for (const requiredFile of ['llms.txt', 'llms-small.txt', 'llms-full.txt']) {
  if (!files.includes(path.join(distRoot, requiredFile))) {
    failures.push(`Missing generated ${requiredFile}.`);
  }
}

const llmsEntryPoint = path.join(distRoot, 'llms.txt');
if (files.includes(llmsEntryPoint)) {
  const llmsText = await readFile(llmsEntryPoint, 'utf8');
  const markdownPageExample =
    'https://dotnet.github.io/orleans/docs/implementation/streams-implementation.md';
  if (!llmsText.includes('Replace the trailing `/` in a page URL with `.md`')) {
    fail(llmsEntryPoint, 'missing per-page Markdown URL guidance');
  }
  if (!llmsText.includes(markdownPageExample)) {
    fail(llmsEntryPoint, 'missing per-page Markdown URL example');
  }
}

if (totalBytes > maxPublishedBytes) {
  failures.push(`Published site is ${(totalBytes / 1024 / 1024).toFixed(1)} MiB; limit is 1024 MiB.`);
}

const apiRoot = path.join(distRoot, 'docs', 'api', 'csharp', 'index.html');
if ((await stat(apiRoot)).size > maxApiRootBytes) {
  failures.push('docs/api/csharp/index.html exceeds 1 MiB; API root navigation is too large.');
}

const suspiciousPatterns = [
  [/\[!VIDEO\b/i, 'unconverted VIDEO directive'],
  [/\[!div\b/i, 'unconverted Learn div directive'],
  [/\[!INCLUDE\b/i, 'unconverted INCLUDE directive'],
  [/&lt;xref:|<xref:/i, 'unconverted xref'],
  [/:::\s*zone(?:-end)?\b/i, 'unconverted zone directive'],
  [/href="(?:%5B|\[)/i, 'Markdown encoded as an href'],
  [
    /data-language="mermaid"|class="[^"]*\blanguage-mermaid\b/i,
    'Mermaid diagram rendered as a code block',
  ],
  [/href="#tab\//i, 'DocFX tab rendered as a heading link'],
];

for (const file of files.filter((candidate) => candidate.endsWith('.html'))) {
  const html = await readFile(file, 'utf8');
  const isRedirect = /http-equiv="refresh"/i.test(html);
  if (file.endsWith(`${path.sep}index.html`) && !isRedirect && relative(file) !== '404.html') {
    const h1Count = [...html.matchAll(/<h1\b/gi)].length;
    if (h1Count !== 1) {
      fail(file, `expected one H1, found ${h1Count}`);
    }
  }
  for (const [pattern, description] of suspiciousPatterns) {
    if (pattern.test(html)) {
      fail(file, description);
    }
  }
}

function isApiMarkdown(file) {
  const filePath = relative(file);
  return filePath === 'docs/api/csharp.md' || filePath.startsWith('docs/api/csharp/');
}

const apiMarkdown = files.filter((file) => file.endsWith('.md') && isApiMarkdown(file));
for (const file of apiMarkdown) {
  const markdown = await readFile(file, 'utf8');
  if (/\bconst\s+static\b/.test(markdown)) {
    fail(file, 'invalid C# modifier order "const static"');
  }
  if (/^# .*\.op_[A-Za-z]/m.test(markdown)) {
    fail(file, 'CLR operator metadata name leaked into heading');
  }
}

const renderedMarkdown = files.filter(
  (file) =>
    file.endsWith('.md') && !isApiMarkdown(file) && path.basename(file) !== 'index.md',
);
if (renderedMarkdown.length === 0) {
  failures.push('No rendered Markdown pages were generated.');
}
for (const file of renderedMarkdown) {
  const markdown = await readFile(file, 'utf8');
  if (!/^# .+/m.test(markdown)) {
    fail(file, 'rendered Markdown has no H1');
  }
  for (const [pattern, description] of [
    [/\[!INCLUDE\b/i, 'unconverted INCLUDE directive'],
    [/:::\s*code\b/i, 'unconverted code directive'],
    [/<xref:|\(xref:/i, 'unconverted xref'],
    [/^\s*import\s+.+\s+from\s+['"]/m, 'MDX import'],
    [/<\/?(?:Aside|Card|CardGrid|Steps|TabItem|Tabs)\b/, 'Starlight MDX component'],
    [/\{\/\*/, 'MDX comment'],
    [/<!doctype html>|<html\b/i, 'HTML document emitted as Markdown'],
  ]) {
    if (pattern.test(markdown)) {
      fail(file, description);
    }
  }
}

const snippetReadme = path.join(
  distRoot,
  'docs',
  'host',
  'snippets',
  'transport-layer-security',
  'README',
  'index.html',
);
if (files.includes(snippetReadme)) {
  failures.push('Snippet support README was published as a conceptual page.');
}

for (const legacyPath of legacyPaths) {
  const outputPath = compatibilityOutputPath(legacyPath, distRoot);
  if (!files.includes(outputPath)) {
    failures.push(`Missing legacy Pages compatibility path '${legacyPath}'.`);
  }
}

for (const [source, target] of Object.entries(redirects)) {
  const outputPath = compatibilityOutputPath(source, distRoot);
  if (!files.includes(outputPath)) {
    failures.push(`Missing explicit compatibility redirect '${source}'.`);
    continue;
  }
  const html = await readFile(outputPath, 'utf8');
  if (!/http-equiv="refresh"/i.test(html) || !html.includes(target) || !html.includes('location.hash')) {
    failures.push(`Compatibility path '${source}' is not an anchor-preserving redirect to '${target}'.`);
  }
}

if (failures.length > 0) {
  console.error(`Rendered output audit found ${failures.length} issue(s):`);
  for (const failure of failures.slice(0, 100)) {
    console.error(`- ${failure}`);
  }
  if (failures.length > 100) {
    console.error(`- ${failures.length - 100} additional issue(s) omitted`);
  }
  process.exit(1);
}

console.log(
  `Rendered output audit passed: ${files.length} files, ${(totalBytes / 1024 / 1024).toFixed(1)} MiB.`,
);
