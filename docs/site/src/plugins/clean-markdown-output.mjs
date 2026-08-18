import { access, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cleanStarlightMarkdown } from 'tidymd';

const mdxComment = /\{\/\*[\s\S]*?\*\/\}\s*/g;
const markdownLink = /(?<!!)\[([^\]]+)\]\(([^)\s]+)\)/g;

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

async function hasMarkdownChildren(directory) {
  if (!(await pathExists(directory))) {
    return false;
  }

  const entries = await readdir(directory, { withFileTypes: true });
  return entries.some((entry) => entry.isFile() && entry.name.endsWith('.md'));
}

export function cleanPublishedMarkdown(source) {
  const markdown = cleanStarlightMarkdown(source, {
    frontmatter: 'title-as-heading',
    internalLinks: { mode: 'preserve' },
  }).replace(mdxComment, '');
  return rewriteDocumentationLinks(markdown);
}

export function rewriteDocumentationLinks(markdown) {
  return markdown.replace(markdownLink, (link, label, href) => {
    const match =
      /^(https:\/\/dotnet\.github\.io)?(\/orleans\/docs(?:\/[^?#]*)?)([?#].*)?$/.exec(href);
    if (!match) {
      return link;
    }

    const [, origin = '', pathname, suffix = ''] = match;
    if (!pathname.endsWith('/')) {
      return link;
    }

    return `[${label}](${origin}${pathname.slice(0, -1)}.md${suffix})`;
  });
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

export async function publishMarkdownOverview(outputRoot, site) {
  const root = path.resolve(outputRoot);
  const docsRoot = path.join(root, 'docs');
  const files = (await pathExists(docsRoot)) ? await collectMarkdown(docsRoot) : [];
  const docsIndex = path.join(root, 'docs.md');
  if (await pathExists(docsIndex)) {
    files.push(docsIndex);
  }

  const overviewFiles = [];
  for (const file of files) {
    if (file === docsIndex) {
      overviewFiles.push(file);
      continue;
    }

    const relativePath = path.relative(docsRoot, file).replaceAll('\\', '/');
    const ownsChildPages = await hasMarkdownChildren(
      file.slice(0, -path.extname(file).length),
    );
    if (relativePath.split('/').length <= 2 && ownsChildPages) {
      overviewFiles.push(file);
    }
  }
  const overviewPaths = new Set(overviewFiles.map((file) => path.resolve(file)));
  const entries = await Promise.all(
    overviewFiles.map(async (file) => {
      const markdown = await readFile(file, 'utf8');
      const title = /^# (.+)$/m.exec(markdown)?.[1];
      if (!title) {
        throw new Error(`Cannot add '${path.relative(root, file)}' to llms.txt overview: missing H1.`);
      }

      const relativePath = path.relative(root, file).replaceAll('\\', '/');
      const docsRelativeSegments = path.relative(docsRoot, file).split(path.sep);
      let depth = file === docsIndex ? 0 : docsRelativeSegments.length;
      if (depth === 2) {
        const parentPage = path.join(docsRoot, `${docsRelativeSegments[0]}.md`);
        if (!overviewPaths.has(path.resolve(parentPage))) {
          depth = 1;
        }
      }
      return {
        depth,
        title,
        url: new URL(relativePath, site).href,
      };
    }),
  );
  entries.sort((left, right) => left.url.localeCompare(right.url));

  const llmsPath = path.join(root, 'llms.txt');
  const llmsText = await readFile(llmsPath, 'utf8');
  const links = entries
    .map((entry) => `${'  '.repeat(entry.depth)}- [${entry.title}](${entry.url})`)
    .join('\n');
  await writeFile(
    llmsPath,
    `${llmsText.trimEnd()}\n\n## Documentation Overview\n\n${links}\n`,
    'utf8',
  );

  return entries.length;
}

export function cleanMarkdownOutput() {
  let site;

  return {
    name: 'orleans-clean-markdown-output',
    hooks: {
      'astro:config:done': ({ config }) => {
        if (!config.site) {
          throw new Error('The Orleans documentation site URL is required for Markdown output.');
        }

        site = config.site;
      },
      'astro:build:done': async ({ dir, logger }) => {
        const outputRoot = fileURLToPath(dir);
        const count = await cleanMarkdownOutputDirectory(outputRoot);
        const overviewCount = await publishMarkdownOverview(outputRoot, site);
        logger.info(`Cleaned ${count} rendered Markdown page${count === 1 ? '' : 's'}.`);
        logger.info(
          `Published ${overviewCount} Markdown overview entr${overviewCount === 1 ? 'y' : 'ies'}.`,
        );
      },
    },
  };
}
