import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  auditDocumentationSources,
  createCsharpFenceManifest,
} from './lib/source-quality.mjs';

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
});

if (process.argv.includes('--update-csharp-fences')) {
  const updated = createCsharpFenceManifest(result.auditedMarkdown, fenceManifest);
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
