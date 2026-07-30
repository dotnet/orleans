import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  collectIncludeTargets,
  collectUidMap,
  convertDocfxMarkdown,
} from './lib/docfx.mjs';
import { prepareGallery } from './lib/gallery.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(siteRoot, '..', '..');
const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
const generatedRoot = path.join(siteRoot, '.generated');
const imageExtensions = new Set(['.gif', '.jpeg', '.jpg', '.png', '.svg', '.webp']);

function isSnippetSupportMarkdown(relativePath) {
  const segments = relativePath.split(path.sep);
  return (
    path.basename(relativePath).toLowerCase() === 'readme.md' &&
    segments.some((segment) => /^snippets(?:-v3)?$/i.test(segment))
  );
}

async function walk(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(entryPath)));
    } else if (entry.isFile()) {
      files.push(entryPath);
    }
  }
  return files;
}

await rm(generatedRoot, { recursive: true, force: true });

const sourceFiles = await walk(sourceRoot);
await Promise.all(
  sourceFiles
    .filter((file) => path.extname(file).toLowerCase() === '.mdx')
    .map((file) => rm(file, { force: true })),
);
const markdownFiles = sourceFiles.filter((file) => path.extname(file).toLowerCase() === '.md');
const includeTargets = await collectIncludeTargets(markdownFiles);
const uidMap = await collectUidMap(markdownFiles, sourceRoot);
let pages = 0;
let assets = 0;

for (const sourcePath of sourceFiles) {
  const relativePath = path.relative(sourceRoot, sourcePath);
  const extension = path.extname(sourcePath).toLowerCase();
  if (extension === '.md') {
    if (includeTargets.has(path.resolve(sourcePath)) || isSnippetSupportMarkdown(relativePath)) {
      continue;
    }
    const outputPath = path.join(
      sourceRoot,
      relativePath.slice(0, -path.extname(relativePath).length) + '.mdx',
    );
    const converted = await convertDocfxMarkdown({
      source: await readFile(sourcePath, 'utf8'),
      sourcePath,
      sourceRoot,
      uidMap,
      editUrl: `https://github.com/dotnet/orleans/edit/main/${path.relative(repositoryRoot, sourcePath).replaceAll('\\', '/')}`,
    });
    await mkdir(path.dirname(outputPath), { recursive: true });
    await writeFile(outputPath, converted, 'utf8');
    pages += 1;
  } else if (imageExtensions.has(extension)) {
    assets += 1;
  }
}

await cp(path.join(siteRoot, 'src', 'site-pages', 'home.mdx'), path.join(sourceRoot, 'index.mdx'));
await cp(path.join(siteRoot, 'src', 'site-pages', 'docs.mdx'), path.join(sourceRoot, 'docs.mdx'));
await cp(path.join(siteRoot, 'src', 'site-pages', 'samples.mdx'), path.join(sourceRoot, 'samples.mdx'));
pages += 3;

const gallery = await prepareGallery({
  repositoryRoot,
  outputFile: path.join(generatedRoot, 'gallery.json'),
  publicImageDirectory: path.join(siteRoot, 'public', 'sample-images'),
});

console.log(
  `Prepared ${pages} documentation pages, ${assets} media assets, and ${gallery.items.length} gallery entries${gallery.missing ? ' (catalog fallback)' : ''}.`,
);
