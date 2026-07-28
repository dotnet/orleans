import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import { prepareGallery, validateGallery } from '../scripts/lib/gallery.mjs';

const temporaryDirectories = [];

async function temporaryDirectory() {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-gallery-'));
  temporaryDirectories.push(directory);
  return directory;
}

afterEach(async () => {
  const { rm } = await import('node:fs/promises');
  await Promise.all(
    temporaryDirectories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

describe('sample gallery', () => {
  test('validates entries and copies local images into the public build', async () => {
    const repositoryRoot = await temporaryDirectory();
    await mkdir(path.join(repositoryRoot, 'samples', 'hello'), { recursive: true });
    await writeFile(path.join(repositoryRoot, 'samples', 'hello', 'preview.png'), 'image');
    await writeFile(
      path.join(repositoryRoot, 'samples', 'gallery.json'),
      JSON.stringify([
        {
          slug: 'hello-world',
          title: 'Hello World',
          description: 'A minimal Orleans app.',
          path: 'samples/hello',
          sourceRepository: 'dotnet/orleans',
          image: 'preview.png',
          languages: ['C#'],
          tags: ['Getting started'],
          featured: true,
        },
      ]),
    );
    const outputFile = path.join(repositoryRoot, 'site', '.generated', 'gallery.json');
    const publicImageDirectory = path.join(repositoryRoot, 'site', 'public', 'sample-images');

    const gallery = await prepareGallery({
      repositoryRoot,
      outputFile,
      publicImageDirectory,
      allowMissing: false,
    });

    expect(gallery.items[0].image).toBe('sample-images/hello-world/preview.png');
    expect(await readFile(path.join(publicImageDirectory, 'hello-world', 'preview.png'), 'utf8')).toBe(
      'image',
    );
  });

  test('emits a development fallback when gallery.json is absent', async () => {
    const repositoryRoot = await temporaryDirectory();
    const outputFile = path.join(repositoryRoot, '.generated', 'gallery.json');
    const result = await prepareGallery({
      repositoryRoot,
      outputFile,
      publicImageDirectory: path.join(repositoryRoot, 'public', 'sample-images'),
    });

    expect(result).toEqual({ missing: true, items: [] });
    expect(JSON.parse(await readFile(outputFile, 'utf8'))).toEqual({ missing: true, items: [] });
  });

  test('rejects malformed entries and path traversal', () => {
    expect(() =>
      validateGallery([
        {
          slug: 'unsafe',
          title: 'Unsafe',
          description: 'Unsafe path.',
          path: '../outside',
          sourceRepository: 'dotnet/orleans',
          image: '',
          languages: [],
          tags: [],
          featured: false,
        },
      ]),
    ).toThrow("unsafe path '../outside'");
  });
});
