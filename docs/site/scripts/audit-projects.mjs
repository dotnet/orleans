import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { auditProjectPolicy } from './lib/project-policy.mjs';

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(siteRoot, '..', '..');
const solutionFile = path.join(repoRoot, 'docs', 'Docs.slnx');
const policy = JSON.parse(
  await readFile(path.join(repoRoot, 'docs', 'project-policy.json'), 'utf8'),
);
const configuredConcurrency = Number.parseInt(
  process.env.ORLEANS_DOCS_PROJECT_AUDIT_CONCURRENCY ?? '8',
  10,
);
const concurrency =
  Number.isInteger(configuredConcurrency) && configuredConcurrency > 0
    ? configuredConcurrency
    : 8;
const result = await auditProjectPolicy({
  repoRoot,
  solutionFile,
  policy,
  concurrency,
});

if (result.issues.length > 0) {
  console.error(`Documentation project audit found ${result.issues.length} issue(s):`);
  for (const issue of result.issues) {
    console.error(
      `- ${issue.project}:1 [${issue.rule}] ${issue.message} Remediation: ${issue.remediation}`,
    );
  }
  process.exit(1);
}

console.log(
  `Documentation project audit passed: ${result.projects.length} projects (${result.docsProjects} docs, ${result.sampleProjects} samples), ${result.solutionEntries} exact solution entries, ${result.orleansPackageReferences} evaluated Orleans package references at ${policy.orleansPackageVersion}, all targeting ${policy.targetFramework}.`,
);
