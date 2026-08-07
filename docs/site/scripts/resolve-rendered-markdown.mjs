import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { collectIncludeTargets } from './lib/include-closure.mjs';
import { markdownDirectiveProtectedLineRanges } from './lib/markdown-ranges.mjs';

const [sourceRootArgument, siteRootArgument] = process.argv.slice(2);
if (!sourceRootArgument || !siteRootArgument) {
  throw new Error('Usage: node resolve-rendered-markdown.mjs <source-root> <site-root>');
}

async function walkMarkdown(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walkMarkdown(entryPath)));
    } else if (entry.isFile() && path.extname(entryPath).toLowerCase() === '.md') {
      files.push(path.resolve(entryPath));
    }
  }
  return files.sort();
}

const sourceRoot = path.resolve(sourceRootArgument);
const sourceFiles = await walkMarkdown(sourceRoot);
const logicalIncludeTargets = new Set();
await collectIncludeTargets(sourceFiles, {
  allowedRoot: path.resolve(siteRootArgument),
  onTarget: (target) => logicalIncludeTargets.add(target.path),
});
const renderedMarkdown = [...new Set([...sourceFiles, ...logicalIncludeTargets])].sort();
process.stdout.write(
  JSON.stringify(
    await Promise.all(
      renderedMarkdown.map(async (file) => {
        const source = await readFile(file, 'utf8');
        return {
          path: file,
          protectedLineRanges: source.includes(':::code')
            ? markdownDirectiveProtectedLineRanges(source)
            : [],
        };
      }),
    ),
  ),
);
