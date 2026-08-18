import { access, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cleanStarlightMarkdown } from 'tidymd';

const mdxComment = /\{\/\*[\s\S]*?\*\/\}\s*/g;

async function pathExists(candidate) {
  try {
    await access(candidate);
    return true;
  } catch {
    return false;
  }
}

async function collectMarkdown(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'api') {
        files.push(...(await collectMarkdown(entryPath)));
      }
    } else if (entry.isFile() && entry.name.endsWith('.md')) {
      files.push(entryPath);
    }
  }
  return files;
}

export function cleanPublishedMarkdown(source) {
  return cleanStarlightMarkdown(source, {
    frontmatter: 'title-as-heading',
    internalLinks: { mode: 'preserve' },
  }).replace(mdxComment, '');
}

export async function cleanMarkdownOutputDirectory(outputRoot) {
  const root = path.resolve(outputRoot);
  const docsRoot = path.join(root, 'docs');
  const files = (await pathExists(docsRoot)) ? await collectMarkdown(docsRoot) : [];
  const docsIndex = path.join(root, 'docs.md');
  if (await pathExists(docsIndex)) {
    files.push(docsIndex);
  }

  await Promise.all(
    files.map(async (file) => {
      const source = await readFile(file, 'utf8');
      await writeFile(file, cleanPublishedMarkdown(source), 'utf8');
    }),
  );

  return files.length;
}

export function cleanMarkdownOutput() {
  return {
    name: 'orleans-clean-markdown-output',
    hooks: {
      'astro:build:done': async ({ dir, logger }) => {
        const count = await cleanMarkdownOutputDirectory(fileURLToPath(dir));
        logger.info(`Cleaned ${count} rendered Markdown page${count === 1 ? '' : 's'}.`);
      },
    },
  };
}
