import { readFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as pagefind from 'pagefind';
import { splitFrontmatter } from './docfx.mjs';

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.wasm', 'application/wasm'],
]);

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

export function devSearchUrl(file, contentRoot, metadata, siteBase) {
  let slug = typeof metadata.slug === 'string' ? metadata.slug : undefined;
  if (!slug) {
    slug = path.relative(contentRoot, file).replaceAll('\\', '/').replace(/\.mdx$/, '');
    if (path.posix.basename(slug).toLowerCase() === 'index') {
      slug = path.posix.dirname(slug);
    }
    if (slug === '.') {
      slug = '';
    }
  }

  const normalizedBase = `/${siteBase.replace(/^\/+|\/+$/g, '')}/`;
  return `${normalizedBase}${slug.replace(/^\/+|\/+$/g, '')}${slug ? '/' : ''}`.replace(
    /\/{2,}/g,
    '/',
  );
}

export function devSearchText(source) {
  return source
    .replace(/^import\s+.+$/gm, ' ')
    .replace(/!\[([^\]]*)\]\([^)]+\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
    .replace(/<[^>]+>/g, ' ')
    .replace(/[`*_#>{}|:]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

export async function generateDevSearchIndex({ contentRoot, outputRoot, siteBase }) {
  await rm(outputRoot, { recursive: true, force: true });
  const response = await pagefind.createIndex({
    forceLanguage: 'en',
    verbose: false,
  });
  if (!response.index || response.errors.length > 0) {
    throw new Error(`Unable to create development search index: ${response.errors.join('; ')}`);
  }

  let entries = 0;
  try {
    for (const file of await walk(contentRoot)) {
      if (!file.endsWith('.mdx')) {
        continue;
      }

      const { metadata, body } = splitFrontmatter(await readFile(file, 'utf8'));
      if (metadata.pagefind === false || typeof metadata.title !== 'string') {
        continue;
      }

      const result = await response.index.addCustomRecord({
        url: devSearchUrl(file, contentRoot, metadata, siteBase),
        content: devSearchText(body),
        language: 'en',
        meta: {
          title: metadata.title,
          ...(typeof metadata.description === 'string'
            ? { description: metadata.description }
            : {}),
        },
      });
      if (result.errors.length > 0) {
        throw new Error(`Unable to index ${file}: ${result.errors.join('; ')}`);
      }
      entries += 1;
    }

    const written = await response.index.writeFiles({ outputPath: outputRoot });
    if (written.errors.length > 0) {
      throw new Error(`Unable to write development search index: ${written.errors.join('; ')}`);
    }
  } finally {
    await pagefind.close();
  }

  return entries;
}

export function devSearchPlugin({ directory, route }) {
  const root = path.resolve(fileURLToPath(directory));
  const routePrefix = `/${route.replace(/^\/+|\/+$/g, '')}/`;

  return {
    name: 'orleans-dev-search',
    configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        let pathname;
        try {
          pathname = decodeURIComponent(
            new URL(request.url ?? '/', 'http://localhost').pathname,
          );
        } catch {
          next();
          return;
        }
        if (!pathname.startsWith(routePrefix)) {
          next();
          return;
        }

        const relative = pathname.slice(routePrefix.length);
        const target = path.resolve(root, ...relative.split('/'));
        const relativeTarget = path.relative(root, target);
        if (
          !relative ||
          relativeTarget === '..' ||
          relativeTarget.startsWith(`..${path.sep}`) ||
          path.isAbsolute(relativeTarget)
        ) {
          response.statusCode = 404;
          response.end();
          return;
        }

        try {
          const content = await readFile(target);
          response.statusCode = 200;
          response.setHeader(
            'Content-Type',
            contentTypes.get(path.extname(target)) ?? 'application/octet-stream',
          );
          response.end(content);
        } catch (error) {
          if (error?.code === 'ENOENT') {
            next();
          } else {
            next(error);
          }
        }
      });
    },
  };
}
