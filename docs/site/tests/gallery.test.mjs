import { mkdtemp, mkdir, readFile, symlink, writeFile } from 'node:fs/promises';
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

async function createSymlinkOrSkip(context, target, link, type) {
  try {
    await symlink(target, link, process.platform === 'win32' && type === 'dir' ? 'junction' : type);
    return true;
  } catch (error) {
    if (['EACCES', 'EINVAL', 'ENOTSUP', 'EPERM', 'UNKNOWN'].includes(error?.code)) {
      context.skip();
      return false;
    }
    throw error;
  }
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
          path: 'hello',
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

  test('accepts samples without preview images', async () => {
    const repositoryRoot = await temporaryDirectory();
    await mkdir(path.join(repositoryRoot, 'samples', 'hello'), { recursive: true });
    await writeFile(
      path.join(repositoryRoot, 'samples', 'gallery.json'),
      JSON.stringify([
        {
          slug: 'hello-world',
          title: 'Hello World',
          description: 'A minimal Orleans app.',
          path: 'hello',
          sourceRepository: 'https://github.com/dotnet/orleans',
          image: null,
          languages: ['C#'],
          tags: ['Getting started'],
          featured: true,
        },
      ]),
    );

    const gallery = await prepareGallery({
      repositoryRoot,
      outputFile: path.join(repositoryRoot, 'site', '.generated', 'gallery.json'),
      publicImageDirectory: path.join(repositoryRoot, 'site', 'public', 'sample-images'),
      allowMissing: false,
    });

    expect(gallery.items[0].image).toBeNull();
  });

  test('rejects catalog entries whose sample directory is missing', async () => {
    const repositoryRoot = await temporaryDirectory();
    await mkdir(path.join(repositoryRoot, 'samples'), { recursive: true });
    await writeFile(
      path.join(repositoryRoot, 'samples', 'gallery.json'),
      JSON.stringify([
        {
          slug: 'missing',
          title: 'Missing',
          description: 'A missing sample.',
          path: 'missing',
          sourceRepository: 'dotnet/orleans',
          image: null,
          languages: ['C#'],
          tags: ['test'],
          featured: false,
        },
      ]),
    );

    await expect(
      prepareGallery({
        repositoryRoot,
        outputFile: path.join(repositoryRoot, '.generated', 'gallery.json'),
        publicImageDirectory: path.join(repositoryRoot, 'public', 'sample-images'),
        allowMissing: false,
      }),
    ).rejects.toThrow("references missing directory 'samples/missing'");
  });

  test('rejects a sample directory symlink or junction to the samples parent', async (context) => {
    const repositoryRoot = await temporaryDirectory();
    const samplesRoot = path.join(repositoryRoot, 'samples');
    await mkdir(samplesRoot, { recursive: true });
    if (
      !(await createSymlinkOrSkip(
        context,
        repositoryRoot,
        path.join(samplesRoot, 'leak'),
        'dir',
      ))
    ) {
      return;
    }
    await writeFile(
      path.join(samplesRoot, 'gallery.json'),
      JSON.stringify([
        {
          slug: 'leak',
          title: 'Leak',
          description: 'An escaping sample directory.',
          path: 'leak',
          sourceRepository: 'dotnet/orleans',
          image: null,
          languages: ['C#'],
          tags: ['test'],
          featured: false,
        },
      ]),
    );

    await expect(
      prepareGallery({
        repositoryRoot,
        outputFile: path.join(repositoryRoot, '.generated', 'gallery.json'),
        publicImageDirectory: path.join(repositoryRoot, 'public', 'sample-images'),
        allowMissing: false,
      }),
    ).rejects.toThrow(
      "Sample 'leak' directory 'samples/leak' resolves outside the samples root",
    );
  });

  test('rejects an image-file symlink outside the samples root', async (context) => {
    const repositoryRoot = await temporaryDirectory();
    const sampleDirectory = path.join(repositoryRoot, 'samples', 'hello');
    const secretFile = path.join(repositoryRoot, 'secret.txt');
    await mkdir(sampleDirectory, { recursive: true });
    await writeFile(secretFile, 'secret');
    if (
      !(await createSymlinkOrSkip(
        context,
        secretFile,
        path.join(sampleDirectory, 'preview.png'),
        'file',
      ))
    ) {
      return;
    }
    await writeFile(
      path.join(repositoryRoot, 'samples', 'gallery.json'),
      JSON.stringify([
        {
          slug: 'hello-world',
          title: 'Hello World',
          description: 'A sample with an escaping image.',
          path: 'hello',
          sourceRepository: 'dotnet/orleans',
          image: 'preview.png',
          languages: ['C#'],
          tags: ['test'],
          featured: false,
        },
      ]),
    );

    await expect(
      prepareGallery({
        repositoryRoot,
        outputFile: path.join(repositoryRoot, '.generated', 'gallery.json'),
        publicImageDirectory: path.join(repositoryRoot, 'public', 'sample-images'),
        allowMissing: false,
      }),
    ).rejects.toThrow(
      "Sample 'hello-world' image 'preview.png' resolves outside the samples root",
    );
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
