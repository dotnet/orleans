import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import {
  auditDocumentationSources,
  collectCsharpFences,
  createCsharpFenceManifest,
  validateCsharpFences,
} from './lib/source-quality.mjs';

const execFileAsync = promisify(execFile);
const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(siteRoot, '..', '..');
const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
const tocPath = path.join(sourceRoot, 'toc.yml');
const manifestPath = path.join(siteRoot, 'src', 'data', 'csharp-fence-exclusions.json');
const packageExclusionsPath = path.join(
  siteRoot,
  'src',
  'data',
  'package-inventory-exclusions.json',
);
const fenceValidatorProject = path.join(
  repositoryRoot,
  'docs',
  'tools',
  'PackageJsonGenerator',
  'PackageJsonGenerator.csproj',
);

let fenceManifest = { files: {} };
try {
  fenceManifest = JSON.parse(await readFile(manifestPath, 'utf8'));
} catch (error) {
  if (error?.code !== 'ENOENT') {
    throw error;
  }
}

const result = await auditDocumentationSources({
  repositoryRoot,
  sourceRoot,
  tocPath,
  fenceManifest,
  packageExclusions: JSON.parse(await readFile(packageExclusionsPath, 'utf8')),
  skipCsharpFenceValidation: true,
});

async function validateFenceSyntax(pages) {
  const fences = pages.flatMap((page) =>
    collectCsharpFences(page.source).map((fence) => ({
      file: page.relativePath,
      ...fence,
    })),
  );
  if (fences.length === 0) {
    return new Set();
  }

  const directory = await mkdtemp(path.join(os.tmpdir(), 'orleans-csharp-fences-'));
  const inputPath = path.join(directory, 'input.json');
  const outputPath = path.join(directory, 'invalid.json');
  try {
    await writeFile(inputPath, JSON.stringify(fences), 'utf8');
    let stdout;
    try {
      ({ stdout } = await execFileAsync(
        'dotnet',
        [
          'run',
          '--project',
          fenceValidatorProject,
          '--configuration',
          'Release',
          '--',
          'validate-csharp-fences',
          '--input',
          inputPath,
          '--output',
          outputPath,
        ],
        {
          cwd: repositoryRoot,
          maxBuffer: 16 * 1024 * 1024,
          windowsHide: true,
        },
      ));
    } catch (error) {
      if (error?.code === 'ENOENT') {
        throw new Error(
          'Inline C# fence validation requires the .NET SDK selected by global.json.',
          { cause: error },
        );
      }
      throw new Error(
        `Inline C# fence validation failed: ${error?.stderr?.trim() || error.message}`,
        { cause: error },
      );
    }
    if (stdout.trim()) {
      console.log(stdout.trim());
    }
    const invalid = JSON.parse(await readFile(outputPath, 'utf8'));
    return new Set(invalid.map((fence) => fence.hash));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

const invalidFenceHashes = await validateFenceSyntax(result.auditedMarkdown);
result.issues.push(
  ...validateCsharpFences(
    result.auditedMarkdown,
    fenceManifest,
    invalidFenceHashes,
  ),
);

if (process.argv.includes('--update-csharp-fences')) {
  const updated = createCsharpFenceManifest(
    result.auditedMarkdown,
    fenceManifest,
    invalidFenceHashes,
  );
  await writeFile(manifestPath, `${JSON.stringify(updated, null, 2)}\n`, 'utf8');
  console.log(`Updated ${path.relative(siteRoot, manifestPath)} for ${Object.keys(updated.files).length} pages.`);
  process.exit(0);
}

if (result.issues.length > 0) {
  console.error(`Documentation source audit found ${result.issues.length} issue(s):`);
  for (const issue of result.issues) {
    console.error(
      `- ${issue.file}:${issue.line} [${issue.rule}] ${issue.message} Remediation: ${issue.remediation}`,
    );
  }
  process.exit(1);
}

console.log(
  `Documentation source audit passed: ${result.pages.length} conceptual pages and ${Object.keys(fenceManifest.files).length} C# fence opt-out files.`,
);
