import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { afterEach, describe, expect, test } from 'vitest';
import {
  devSearchPlugin,
  devSearchText,
  devSearchUrl,
} from '../scripts/lib/dev-search.mjs';

const temporaryDirectories = [];

afterEach(async () => {
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true })),
  );
});

describe('development search', () => {
  test('creates deployed URLs from generated slugs', () => {
    expect(
      devSearchUrl(
        path.join('content', 'docs', 'overview.mdx'),
        path.join('content', 'docs'),
        { slug: 'docs/overview' },
        '/orleans/',
      ),
    ).toBe('/orleans/docs/overview/');
  });

  test('creates URLs for site pages without generated slugs', () => {
    expect(
      devSearchUrl(
        path.join('content', 'docs', 'index.mdx'),
        path.join('content', 'docs'),
        {},
        '/orleans/',
      ),
    ).toBe('/orleans/');
    expect(
      devSearchUrl(
        path.join('content', 'docs', 'samples.mdx'),
        path.join('content', 'docs'),
        {},
        '/orleans/',
      ),
    ).toBe('/orleans/samples/');
  });

  test('removes Markdown and MDX syntax from indexed content', () => {
    expect(
      devSearchText(
        'import Card from "./Card.astro";\n\n## Heading\n\n[Orleans](overview.md) uses `grains`.\n\n<Card />',
      ),
    ).toBe('Heading Orleans uses grains .');
  });

  test('serves generated Pagefind assets under the site base path', async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-dev-search-'));
    temporaryDirectories.push(directory);
    await writeFile(path.join(directory, 'pagefind.js'), 'export const search = true;');

    let middleware;
    devSearchPlugin({
      directory: pathToFileURL(`${directory}${path.sep}`),
      route: '/orleans/pagefind/',
    }).configureServer({
      middlewares: {
        use(value) {
          middleware = value;
        },
      },
    });

    const headers = new Map();
    let body;
    const response = {
      setHeader(name, value) {
        headers.set(name, value);
      },
      end(value) {
        body = value;
      },
    };
    await middleware({ url: '/orleans/pagefind/pagefind.js' }, response, () => {
      throw new Error('Search asset middleware unexpectedly delegated the request.');
    });

    expect(response.statusCode).toBe(200);
    expect(headers.get('Content-Type')).toBe('text/javascript; charset=utf-8');
    expect(body.toString()).toBe('export const search = true;');
  });

  test('rejects traversal outside the generated search directory', async () => {
    const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-dev-search-'));
    temporaryDirectories.push(directory);

    let middleware;
    devSearchPlugin({
      directory: pathToFileURL(`${directory}${path.sep}`),
      route: '/orleans/pagefind/',
    }).configureServer({
      middlewares: {
        use(value) {
          middleware = value;
        },
      },
    });

    const response = {
      end() {},
    };
    await middleware({ url: '/orleans/pagefind/..%2Fsecret' }, response, () => {
      throw new Error('Traversal request unexpectedly delegated to another middleware.');
    });

    expect(response.statusCode).toBe(404);
  });
});
