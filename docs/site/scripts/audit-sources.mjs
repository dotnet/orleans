import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { auditDocumentationSources } from './lib/source-quality.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(siteRoot, '..', '..');
const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
const tocPath = path.join(sourceRoot, 'toc.yml');
const packageExclusionsPath = path.join(
  siteRoot,
  'src',
  'data',
  'package-inventory-exclusions.json',
);

const result = await auditDocumentationSources({
  repositoryRoot,
  sourceRoot,
  tocPath,
  packageExclusions: JSON.parse(await readFile(packageExclusionsPath, 'utf8')),
});

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
  `Documentation source audit passed: ${result.pages.length} conceptual pages; all C# examples use compiled :::code sources.`,
);
