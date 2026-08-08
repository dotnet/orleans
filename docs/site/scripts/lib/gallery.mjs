import { copyFile, lstat, mkdir, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

function assertString(value, field, index) {
  if (typeof value !== 'string') {
    throw new Error(`samples/gallery.json entry ${index} has a non-string '${field}' value.`);
  }
  return value;
}

function assertNullableString(value, field, index) {
  return value === null ? null : assertString(value, field, index);
}

function assertStringArray(value, field, index) {
  if (!Array.isArray(value) || value.some((item) => typeof item !== 'string')) {
    throw new Error(`samples/gallery.json entry ${index} must contain a string array '${field}'.`);
  }
  return [...new Set(value.map((item) => item.trim()).filter(Boolean))];
}

export function validateGallery(value) {
  if (!Array.isArray(value)) {
    throw new Error('samples/gallery.json must contain an array.');
  }

  const slugs = new Set();
  return value.map((entry, index) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new Error(`samples/gallery.json entry ${index} must be an object.`);
    }

    const expected = [
      'description',
      'featured',
      'image',
      'languages',
      'path',
      'slug',
      'sourceRepository',
      'tags',
      'title',
    ];
    const missing = expected.filter((field) => !Object.hasOwn(entry, field));
    const unknown = Object.keys(entry).filter((field) => !expected.includes(field));
    if (missing.length > 0 || unknown.length > 0) {
      throw new Error(
        `samples/gallery.json entry ${index} has missing fields [${missing.join(', ')}] and unknown fields [${unknown.join(', ')}].`,
      );
    }

    const slug = assertString(entry.slug, 'slug', index);
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug)) {
      throw new Error(`samples/gallery.json entry ${index} has invalid slug '${slug}'.`);
    }
    if (slugs.has(slug)) {
      throw new Error(`samples/gallery.json contains duplicate slug '${slug}'.`);
    }
    slugs.add(slug);

    if (typeof entry.featured !== 'boolean') {
      throw new Error(`samples/gallery.json entry ${index} has a non-boolean 'featured' value.`);
    }

    const samplePath = assertString(entry.path, 'path', index).replaceAll('\\', '/');
    if (path.posix.isAbsolute(samplePath) || samplePath.split('/').includes('..')) {
      throw new Error(`samples/gallery.json entry ${index} has unsafe path '${samplePath}'.`);
    }

    const image = assertNullableString(entry.image, 'image', index)?.replaceAll('\\', '/') ?? null;
    if (image && !/^https?:\/\//.test(image)) {
      if (path.posix.isAbsolute(image) || image.split('/').includes('..')) {
        throw new Error(`samples/gallery.json entry ${index} has unsafe image path '${image}'.`);
      }
    }

    return {
      slug,
      title: assertString(entry.title, 'title', index),
      description: assertString(entry.description, 'description', index),
      path: samplePath,
      sourceRepository: assertString(entry.sourceRepository, 'sourceRepository', index),
      image,
      languages: assertStringArray(entry.languages, 'languages', index),
      tags: assertStringArray(entry.tags, 'tags', index),
      featured: entry.featured,
    };
  });
}

async function readGallery(galleryPath, allowMissing) {
  let source;
  try {
    source = await readFile(galleryPath, 'utf8');
  } catch (error) {
    if (allowMissing && error?.code === 'ENOENT') {
      return { missing: true, items: [] };
    }
    throw error;
  }

  let parsed;
  try {
    parsed = JSON.parse(source);
  } catch (error) {
    throw new Error(`Could not parse ${galleryPath}: ${error.message}`, { cause: error });
  }
  return { missing: false, items: validateGallery(parsed) };
}

function isWithin(root, target) {
  const relative = path.relative(root, target);
  return (
    relative === '' ||
    (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative))
  );
}

async function physicalSamplesRoot(repositoryRoot) {
  const physicalRepositoryRoot = await realpath(repositoryRoot);
  const logicalSamplesRoot = path.join(repositoryRoot, 'samples');
  await lstat(logicalSamplesRoot);
  const samplesRoot = await realpath(logicalSamplesRoot);
  if (!isWithin(physicalRepositoryRoot, samplesRoot)) {
    throw new Error(
      `The repository samples directory '${logicalSamplesRoot}' resolves outside the repository root ('${samplesRoot}').`,
    );
  }
  return samplesRoot;
}

async function resolveExistingCandidate(candidate) {
  try {
    await lstat(candidate);
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return undefined;
    }
    throw error;
  }
  return realpath(candidate);
}

async function resolveLocalImage(sample, repositoryRoot, samplesRoot, sampleDirectory) {
  if (!sample.image) {
    return undefined;
  }
  if (/^https?:\/\//.test(sample.image)) {
    return sample.image;
  }

  const candidates = [
    path.resolve(repositoryRoot, sample.image),
    path.resolve(repositoryRoot, 'samples', sample.image),
    path.resolve(sampleDirectory, sample.image),
  ];
  for (const candidate of candidates) {
    const physicalCandidate = await resolveExistingCandidate(candidate);
    if (!physicalCandidate) {
      continue;
    }
    if (!isWithin(samplesRoot, physicalCandidate)) {
      throw new Error(
        `Sample '${sample.slug}' image '${sample.image}' resolves outside the samples root ('${physicalCandidate}').`,
      );
    }
    if (!(await stat(physicalCandidate)).isFile()) {
      throw new Error(
        `Sample '${sample.slug}' image '${sample.image}' does not resolve to a regular file ('${physicalCandidate}').`,
      );
    }
    return physicalCandidate;
  }

  throw new Error(
    `Sample '${sample.slug}' references missing image '${sample.image}'. Checked ${candidates.join(' and ')}.`,
  );
}

async function validateSamplePath(sample, repositoryRoot, samplesRoot) {
  const samplePath = path.resolve(repositoryRoot, 'samples', sample.path);
  try {
    await lstat(samplePath);
    const physicalSamplePath = await realpath(samplePath);
    if (!isWithin(samplesRoot, physicalSamplePath)) {
      throw new Error(
        `Sample '${sample.slug}' directory 'samples/${sample.path}' resolves outside the samples root ('${physicalSamplePath}').`,
      );
    }
    if (!(await stat(physicalSamplePath)).isDirectory()) {
      throw new Error('not a directory');
    }
    return physicalSamplePath;
  } catch (error) {
    if (
      error?.code !== 'ENOENT' &&
      error?.message !== 'not a directory'
    ) {
      throw error;
    }
    throw new Error(
      `Sample '${sample.slug}' references missing directory 'samples/${sample.path}'. Update samples/gallery.json or restore the sample.`,
      { cause: error },
    );
  }
}

export async function prepareGallery({
  repositoryRoot,
  outputFile,
  publicImageDirectory,
  allowMissing = true,
}) {
  const galleryPath = path.join(repositoryRoot, 'samples', 'gallery.json');
  const gallery = await readGallery(galleryPath, allowMissing);
  await rm(publicImageDirectory, { recursive: true, force: true });
  const samplesRoot =
    gallery.items.length > 0 ? await physicalSamplesRoot(repositoryRoot) : undefined;

  const items = [];
  for (const sample of gallery.items) {
    const sampleDirectory = await validateSamplePath(sample, repositoryRoot, samplesRoot);
    const sourceImage = await resolveLocalImage(
      sample,
      repositoryRoot,
      samplesRoot,
      sampleDirectory,
    );
    let image = null;
    if (sourceImage && /^https?:\/\//.test(sourceImage)) {
      image = sourceImage;
    } else if (sourceImage) {
      const destinationDirectory = path.join(publicImageDirectory, sample.slug);
      const filename = path.basename(sourceImage);
      await mkdir(destinationDirectory, { recursive: true });
      await copyFile(sourceImage, path.join(destinationDirectory, filename));
      image = path.posix.join('sample-images', sample.slug, filename);
    }
    items.push({ ...sample, image });
  }

  await mkdir(path.dirname(outputFile), { recursive: true });
  await writeFile(
    outputFile,
    `${JSON.stringify({ missing: gallery.missing, items }, null, 2)}\n`,
    'utf8',
  );
  return { missing: gallery.missing, items };
}
