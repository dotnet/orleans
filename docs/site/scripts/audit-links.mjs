import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  auditRenderedInternalLinks,
  auditSourceLinks,
  collectLinkAuditDocuments,
  collectXmlDocumentationExternalUrls,
  probeExternalTargets,
} from './lib/link-audit.mjs';
import allowlist from '../src/data/external-link-allowlist.json' with { type: 'json' };

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(siteRoot, '..', '..');
const sourceRoot = path.join(siteRoot, 'src', 'content', 'docs');
const includeRoot = path.join(siteRoot, 'src');
const distRoot = path.join(siteRoot, 'dist');
const sourceCodeRoot = path.join(repositoryRoot, 'src');

const documents = await collectLinkAuditDocuments({ sourceRoot, allowedRoot: includeRoot });
const sourceAudit = await auditSourceLinks({ documents, sourceRoot });
const renderedIssues = await auditRenderedInternalLinks({
  distRoot,
  repositoryRoot,
  internalProvenance: sourceAudit.internalProvenance,
  externalTargets: sourceAudit.externalTargets,
});
const xmlDocumentationExternalUrls =
  await collectXmlDocumentationExternalUrls(sourceCodeRoot);
const externalAudit = await probeExternalTargets({
  externalTargets: sourceAudit.externalTargets,
  allowlist,
  allowlistReferences: xmlDocumentationExternalUrls,
});

for (const warning of externalAudit.warnings) {
  console.warn(`External link warning: ${warning}`);
}
const failures = [
  ...sourceAudit.issues.map(
    (issue) =>
      `${issue.file}:${issue.line} [${issue.rule}] ${issue.message} Remediation: ${issue.remediation}`,
  ),
  ...renderedIssues,
  ...externalAudit.failures,
];
if (failures.length > 0) {
  console.error(`Link audit found ${failures.length} issue(s):`);
  for (const failure of failures.slice(0, 200)) console.error(`- ${failure}`);
  process.exit(1);
}
console.log(
  `Link audit passed: ${sourceAudit.references.length} source references; ${sourceAudit.internalProvenance.size} internal route targets and ${sourceAudit.externalTargets.size} external URLs after deduplication; rendered routes/anchors valid (${externalAudit.probed} external URLs probed).`,
);
