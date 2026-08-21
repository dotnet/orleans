import { execFile } from 'node:child_process';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fromMarkdown } from 'mdast-util-from-markdown';
import { gfmTableFromMarkdown } from 'mdast-util-gfm-table';
import { gfmTable } from 'micromark-extension-gfm-table';
import {
  collectIncludeTargets,
  isDocumentationFragmentMarkdown,
  readTocItems,
  splitFrontmatter,
} from './docfx.mjs';

const renderedOrleansReleasePatterns = [
  /(?<!Microsoft\.)\bOrleans(?:\s+(?:version|release)\s*:?\s*v?|\s*:\s*v?|\s+v?)(\d+)(?:\.(?:\d+|x))*\b/gi,
  /\b(?:version|release)\s*:?\s*v?(\d+)(?:\.(?:\d+|x))*\s+of\s+(?:Microsoft\s+)?Orleans(?![.\w])/gi,
];
const namedOrleansPivotPattern = /\borleans-(\d+)-(?:\d+|x)\b/gi;
const versionSpecificPathPattern =
  /(?:^|\/)(?:migration(?:\/|-guide(?:\.md)?$)|upgrades?(?:\/|\.md$))/i;
const execFileAsync = promisify(execFile);

function toPosix(value) {
  return value.split(path.sep).join('/');
}

export function isNavigationHidden(source) {
  return splitFrontmatter(source).metadata.navigation === 'hidden';
}

async function walk(directory, predicate = () => true) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(entryPath, predicate)));
    } else if (entry.isFile() && predicate(entryPath)) {
      files.push(entryPath);
    }
  }
  return files.sort();
}

function lineNumberAt(source, index) {
  return source.slice(0, index).split('\n').length;
}

function diagnostic(rule, file, line, message, remediation) {
  return { rule, file, line, message, remediation };
}

function markdownNodesFromTree(tree, acceptedTypes) {
  const matches = [];
  const pending = [tree];
  while (pending.length > 0) {
    const node = pending.pop();
    if (acceptedTypes.has(node.type)) {
      matches.push(node);
    }
    if (Array.isArray(node.children)) {
      pending.push(...node.children);
    }
  }
  return matches.sort((left, right) => left.position.start.offset - right.position.start.offset);
}

function markdownNodes(source, acceptedTypes) {
  return markdownNodesFromTree(fromMarkdown(source), acceptedTypes);
}

function renderedText(node, source, lineStarts) {
  let value = '';
  const offsets = [];
  const append = (text, position) => {
    const parts = text.split('\n');
    let sourceLine = position.start.line - 1;
    for (let partIndex = 0; partIndex < parts.length; partIndex += 1) {
      const part = parts[partIndex];
      const lineStart = lineStarts[sourceLine] ?? position.start.offset;
      const lineEnd = source.indexOf('\n', lineStart);
      const sourceLineText = source.slice(lineStart, lineEnd < 0 ? source.length : lineEnd);
      const searchStart = partIndex === 0 ? position.start.column - 1 : 0;
      const column = sourceLineText.indexOf(part, searchStart);
      const partOffset = lineStart + (column < 0 ? searchStart : column);
      value += part;
      for (let index = 0; index < part.length; index += 1) {
        offsets.push(partOffset + index);
      }
      if (partIndex < parts.length - 1) {
        value += '\n';
        offsets.push(lineEnd < 0 ? source.length : lineEnd);
        sourceLine += 1;
      }
    }
  };
  const pending = [...(node.children ?? [])].reverse();
  while (pending.length > 0) {
    const child = pending.pop();
    if (child.type === 'text' || child.type === 'inlineCode') {
      append(child.value, child.position);
    } else if (child.type === 'break') {
      value += '\n';
      offsets.push(child.position.start.offset);
    } else if (child.type === 'image' && child.alt) {
      append(child.alt, child.position);
    } else if (Array.isArray(child.children)) {
      pending.push(...child.children.toReversed());
    }
  }
  return { value, offsets };
}

function htmlTagEnd(source, start, end) {
  let quote;
  for (let index = start + 1; index < end; index += 1) {
    const character = source[index];
    if (quote) {
      if (character === quote) {
        quote = undefined;
      }
    } else if (character === '"' || character === "'") {
      quote = character;
    } else if (character === '>') {
      return index;
    }
  }
  return end - 1;
}

function htmlCommentEnd(source, start, end) {
  const standard = source.indexOf('-->', start);
  const bang = source.indexOf('--!>', start);
  if (standard < 0 && bang < 0) return { index: end, length: 0 };
  if (bang >= 0 && (standard < 0 || bang < standard)) {
    return { index: bang, length: 4 };
  }
  return { index: standard, length: 3 };
}

function visibleHtmlText(node, source) {
  const value = [];
  const offsets = [];
  const start = node.position.start.offset;
  const end = node.position.end.offset;
  const hiddenElements = new Set(['code', 'pre', 'script', 'style']);
  for (let index = start; index < end; ) {
    if (source.startsWith('<!--', index)) {
      const commentEnd = htmlCommentEnd(source, index + 4, end);
      index = commentEnd.index + commentEnd.length;
      continue;
    }
    if (source[index] === '<') {
      const tagEnd = htmlTagEnd(source, index, end);
      const tag = /^<\s*(\/?)\s*([a-z][a-z\d-]*)/i.exec(source.slice(index, tagEnd + 1));
      if (tag && !tag[1] && hiddenElements.has(tag[2].toLowerCase())) {
        const closePattern = new RegExp(`<\\/\\s*${tag[2]}\\s*>`, 'gi');
        closePattern.lastIndex = tagEnd + 1;
        const close = closePattern.exec(source);
        index = close && close.index < end ? close.index + close[0].length : end;
      } else {
        index = tagEnd + 1;
      }
      continue;
    }
    const whitespaceEntity = /^(?:&nbsp;|&#0*32;|&#x0*20;)/i.exec(source.slice(index, end));
    if (whitespaceEntity) {
      value.push(' ');
      offsets.push(index);
      index += whitespaceEntity[0].length;
      continue;
    }
    value.push(source[index]);
    offsets.push(index);
    index += 1;
  }
  return { value: value.join(''), offsets };
}

function matchesInRenderedText(rendered, fallbackOffset) {
  return renderedOrleansReleasePatterns.flatMap((pattern) =>
    [...rendered.value.matchAll(pattern)].map((match) => ({
      text: match[0],
      index: rendered.offsets[match.index] ?? fallbackOffset,
    })),
  );
}

function renderedReleaseMatches(tree, source) {
  const lineStarts = [0];
  for (const match of source.matchAll(/\n/g)) {
    lineStarts.push(match.index + 1);
  }
  return [
    ...markdownNodesFromTree(tree, new Set(['heading', 'paragraph', 'tableCell'])).flatMap(
      (node) => {
        const rendered = renderedText(node, source, lineStarts);
        return matchesInRenderedText(rendered, node.position.start.offset);
      },
    ),
    ...markdownNodesFromTree(tree, new Set(['html'])).flatMap((node) =>
      matchesInRenderedText(visibleHtmlText(node, source), node.position.start.offset),
    ),
  ];
}

function resolvesToVersionSpecificPage(url, relativePath) {
  if (/^[a-z][a-z\d+.-]*:|^\/\//i.test(url)) {
    return false;
  }
  const pathname = url.split(/[?#]/, 1)[0].replaceAll('\\', '/');
  const resolved = pathname.startsWith('/')
    ? path.posix.normalize(pathname)
    : path.posix.normalize(path.posix.join(path.posix.dirname(relativePath), pathname));
  return versionSpecificPathPattern.test(resolved);
}

function isMigrationLink(matchIndex, links, relativePath) {
  return links.some(
    (link) =>
      link.position.start.offset <= matchIndex &&
      matchIndex < link.position.end.offset &&
      resolvesToVersionSpecificPage(link.url, relativePath),
  );
}

function markdownCodeBlocks(source) {
  return markdownNodes(source, new Set(['code']));
}

function rangesForNodes(nodes) {
  return nodes.map((node) => [
    node.position.start.offset,
    node.position.end.offset,
  ]);
}

function isInsideRanges(index, ranges) {
  return ranges.some(([start, end]) => start <= index && index < end);
}

export function findReleaseVersionIssues({ source, relativePath }) {
  const normalizedPath = path.posix.normalize(relativePath.replaceAll('\\', '/'));
  if (versionSpecificPathPattern.test(normalizedPath)) {
    return [];
  }

  const tree = fromMarkdown(source);
  const directiveProtectedNodes = markdownNodesFromTree(tree, new Set(['code', 'html']));
  const codeRanges = rangesForNodes(
    directiveProtectedNodes.filter((node) => node.type === 'code'),
  );
  const links = markdownNodesFromTree(tree, new Set(['link']));
  const definitions = new Map(
    markdownNodesFromTree(tree, new Set(['definition'])).map((definition) => [
      definition.identifier,
      definition.url,
    ]),
  );
  links.push(
    ...markdownNodesFromTree(tree, new Set(['linkReference']))
      .filter((link) => definitions.has(link.identifier))
      .map((link) => ({ ...link, url: definitions.get(link.identifier) })),
  );
  const matches = [
    ...renderedReleaseMatches(tree, source),
    ...[...source.matchAll(namedOrleansPivotPattern)].map((match) => ({
      text: match[0],
      index: match.index,
    })),
  ];
  const uniqueMatches = [
    ...new Map(matches.map((match) => [`${match.index}:${match.text}`, match])).values(),
  ].sort((left, right) => left.index - right.index);
  return uniqueMatches
    .filter(
      (match) =>
        !isInsideRanges(match.index, codeRanges) &&
        !isMigrationLink(match.index, links, normalizedPath),
    )
    .map((match) =>
      diagnostic(
        'DOCS001',
        normalizedPath,
        lineNumberAt(source, match.index),
        `Explicit Orleans release reference '${match.text}' appears in current-release/versionless documentation.`,
        'Describe current behavior without an Orleans release number, or move version-specific guidance into migration/ or upgrade documentation and link to it.',
      ),
    );
}

function flattenToc(items, ancestors = []) {
  return items.flatMap((item) => {
    const current = [...ancestors, item.name];
    return [
      ...(typeof item.href === 'string' ? [{ ...item, ancestors, trail: current }] : []),
      ...(Array.isArray(item.items) ? flattenToc(item.items, current) : []),
    ];
  });
}

function findGroup(items, name) {
  for (const item of items) {
    if (item.name === name) {
      return item;
    }
    if (Array.isArray(item.items)) {
      const nested = findGroup(item.items, name);
      if (nested) {
        return nested;
      }
    }
  }
  return undefined;
}

function hrefLine(tocSource, href) {
  const escaped = href.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = new RegExp(`^\\s*href:\\s*['"]?${escaped}['"]?\\s*$`, 'm').exec(tocSource);
  return match ? lineNumberAt(tocSource, match.index) : 1;
}

export function validateNavigation({
  markdownPaths,
  excludedPaths,
  tocItems,
  tocSource = '',
  tocFile = 'toc.yml',
}) {
  const issues = [];
  const pages = new Set(markdownPaths.filter((item) => !excludedPaths.includes(item)));
  const entries = flattenToc(tocItems).filter(
    (item) => {
      const href = item.href?.split(/[?#]/, 1)[0];
      return (
        typeof href === 'string' &&
        !href.startsWith('/') &&
        !/^https?:\/\//i.test(href) &&
        path.posix.extname(href).toLowerCase() === '.md'
      );
    },
  );
  const counts = new Map();
  for (const entry of entries) {
    const target = path.posix.normalize(entry.href.split(/[?#]/, 1)[0]);
    counts.set(target, (counts.get(target) ?? 0) + 1);
    if (!markdownPaths.includes(target)) {
      issues.push(
        diagnostic(
          'DOCS002',
          tocFile,
          hrefLine(tocSource, entry.href),
          `Navigation target '${entry.href}' does not exist.`,
          'Correct the href or add the conceptual Markdown page.',
        ),
      );
    }
  }

  for (const page of pages) {
    const count = counts.get(page) ?? 0;
    if (count === 0) {
      issues.push(
        diagnostic(
          'DOCS002',
          page,
          1,
          'Maintained conceptual page is missing from toc.yml.',
          'Add this page to toc.yml exactly once, or make it an explicit include/snippet support file.',
        ),
      );
    } else if (count > 1) {
      issues.push(
        diagnostic(
          'DOCS002',
          tocFile,
          hrefLine(tocSource, page),
          `Navigation target '${page}' appears ${count} times.`,
          'Keep exactly one toc.yml entry for every maintained conceptual page.',
        ),
      );
    }
  }

  const architecture = tocItems.find((item) => item.name === 'Architecture and internals');
  const architectureLinks = architecture ? flattenToc(architecture.items ?? []) : [];
  if (
    !architecture ||
    !architectureLinks.some((item) => item.href === 'implementation/index.md')
  ) {
    issues.push(
      diagnostic(
        'DOCS003',
        tocFile,
        1,
        "The top-level 'Architecture and internals' section is missing its runtime overview.",
        "Preserve 'Architecture and internals' as a first-class section containing implementation/index.md.",
      ),
    );
  }

  const eventSourcing = findGroup(tocItems, 'Event Sourcing');
  const eventSourcingLinks = eventSourcing ? flattenToc(eventSourcing.items ?? []) : [];
  if (
    !eventSourcing ||
    !eventSourcingLinks.some((item) => item.href === 'grains/event-sourcing/index.md')
  ) {
    issues.push(
      diagnostic(
        'DOCS003',
        tocFile,
        1,
        'Supported Event Sourcing documentation is not navigated as current content.',
        "Keep an 'Event Sourcing' navigation group containing grains/event-sourcing/index.md.",
      ),
    );
  }

  return issues;
}

export function collectCsharpFences(source) {
  return markdownCodeBlocks(source)
    .filter((node) => /^(?:c#|csharp|cs)$/i.test(node.lang ?? ''))
    .map((node) => ({
      line: node.position.start.line,
      source: node.value,
    }));
}

export function validateCsharpFences(pages) {
  return pages.flatMap((page) =>
    collectCsharpFences(page.source).map((fence) =>
      diagnostic(
        'DOCS004',
        page.relativePath,
        fence.line,
        'Inline C# fences are not compiler-checked.',
        'Move the example into a named region in a compiled snippet project and reference it with :::code.',
      ),
    ),
  );
}

function extractTableCodeValues(source, column) {
  const values = [];
  for (const line of source.split('\n')) {
    if (!line.startsWith('|') || /^[-:|\s]+$/.test(line)) {
      continue;
    }
    const cells = line
      .slice(1, -1)
      .split('|')
      .map((cell) => cell.trim());
    const match = /^`([^`]+)`$/.exec(cells[column] ?? '');
    if (match) {
      values.push({ value: match[1], cells, line: lineNumberAt(source, source.indexOf(line)) });
    }
  }
  return values;
}

async function evaluatePackageProject(file) {
  const { stdout } = await execFileAsync(
    'dotnet',
    [
      'msbuild',
      file,
      '-nologo',
      '-getProperty:IsPackable',
      '-getProperty:PackageId',
      '-getProperty:VersionSuffix',
    ],
    {
      maxBuffer: 4 * 1024 * 1024,
      windowsHide: true,
      env: {
        ...process.env,
        DOTNET_NOLOGO: 'true',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
      },
    },
  );
  return JSON.parse(stdout).Properties;
}

export async function collectPackageProjects(
  repositoryRoot,
  { evaluate = evaluatePackageProject, concurrency = 8 } = {},
) {
  const files = await walk(
    path.join(repositoryRoot, 'src'),
    (item) => item.endsWith('.csproj'),
  );
  const evaluations = new Array(files.length);
  let next = 0;
  async function worker() {
    while (next < files.length) {
      const index = next;
      next += 1;
      evaluations[index] = await evaluate(files[index]);
    }
  }
  await Promise.all(
    Array.from({ length: Math.min(concurrency, files.length || 1) }, () => worker()),
  );

  const projects = new Map();
  for (let index = 0; index < files.length; index += 1) {
    const file = files[index];
    const properties = evaluations[index];
    if (String(properties.IsPackable).toLowerCase() !== 'true') {
      continue;
    }
    const packageId = String(properties.PackageId);
    if (!packageId) {
      throw new Error(`Packable project '${file}' evaluates to an empty PackageId.`);
    }
    if (projects.has(packageId)) {
      throw new Error(
        `Packable projects '${projects.get(packageId).file}' and '${file}' share PackageId '${packageId}'.`,
      );
    }
    projects.set(packageId, {
      file,
      alpha: String(properties.VersionSuffix).startsWith('alpha.'),
    });
  }
  return projects;
}

function markdownText(node) {
  if (node.type === 'text' || node.type === 'inlineCode') {
    return node.value;
  }
  return Array.isArray(node.children) ? node.children.map(markdownText).join('') : '';
}

function findPackageLink(node) {
  if (node.type === 'link' || node.type === 'linkReference') {
    return node;
  }
  if (!Array.isArray(node.children)) {
    return undefined;
  }
  return node.children.map(findPackageLink).find(Boolean);
}

function containsHtmlAnchor(node) {
  if (node.type === 'html' && /<\s*\/?a\b/i.test(node.value)) {
    return true;
  }
  return Array.isArray(node.children) && node.children.some(containsHtmlAnchor);
}

function nugetPackageId(target) {
  return /^https:\/\/(?:www\.)?nuget\.org\/packages\/(Microsoft\.Orleans(?:\.[A-Za-z0-9]+)+)\/?(?:[?#].*)?$/i.exec(
    target,
  )?.[1];
}

export function parseDocumentedPackageTable(packagePage, packageColumn = 0, statusColumn) {
  const tree = fromMarkdown(packagePage, {
    extensions: [gfmTable()],
    mdastExtensions: [gfmTableFromMarkdown()],
  });
  const definitions = new Map();
  const tables = [];
  const pending = [tree];
  while (pending.length > 0) {
    const node = pending.pop();
    if (node.type === 'definition') {
      definitions.set(node.identifier, node.url);
    } else if (node.type === 'table') {
      tables.push(node);
    }
    if (Array.isArray(node.children)) {
      pending.push(...node.children);
    }
  }

  const documentedPackages = new Map();
  const documentedAlpha = new Map();
  const documentedStatus = new Map();
  const issues = [];
  for (const table of tables.sort((left, right) => left.position.start.offset - right.position.start.offset)) {
    for (const row of table.children.slice(1)) {
      const packageCell = row.children[packageColumn];
      if (!packageCell) {
        continue;
      }
      let visiblePackageId = markdownText(packageCell).trim();
      const linkNode = findPackageLink(packageCell);
      let unresolvedReference;
      if (!linkNode) {
        const referenceSyntax = /^\[([^\]]+)\]\[([^\]]+)\]$/.exec(visiblePackageId);
        if (referenceSyntax) {
          visiblePackageId = referenceSyntax[1].replace(/^`|`$/g, '').trim();
          unresolvedReference = referenceSyntax[2].trim().toLowerCase();
        }
      }
      if (!/^Microsoft\.Orleans(?:\.[A-Za-z0-9]+)+$/.test(visiblePackageId)) {
        continue;
      }
      const line = row.position.start.line;
      documentedPackages.set(visiblePackageId, line);
      documentedAlpha.set(
        visiblePackageId,
        /^alpha(?:\b|:)/i.test(
          row.children.slice(1).map(markdownText).join(' ').trim(),
        ),
      );
      if (statusColumn !== undefined && row.children[statusColumn]) {
        documentedStatus.set(
          visiblePackageId,
          markdownText(row.children[statusColumn]).replaceAll('*', '').trim(),
        );
      }

      if (containsHtmlAnchor(packageCell)) {
        issues.push(
          diagnostic(
            'DOCS005',
            'resources/nuget-packages.md',
            line,
            `Package link for '${visiblePackageId}' uses raw HTML and cannot be validated.`,
            'Use a Markdown link to the matching NuGet package page.',
          ),
        );
        continue;
      }
      if (!linkNode && !unresolvedReference) {
        continue;
      }
      const reference =
        linkNode?.type === 'linkReference' ? linkNode.identifier : unresolvedReference;
      const target = linkNode?.type === 'link' ? linkNode.url : definitions.get(reference);
      const linkedPackageId = target ? nugetPackageId(target) : undefined;
      if (!target) {
        issues.push(
          diagnostic(
            'DOCS005',
            'resources/nuget-packages.md',
            line,
            `Package link for '${visiblePackageId}' uses unresolved reference '${reference}'.`,
            'Define the reference target as the matching NuGet package page.',
          ),
        );
      } else if (!linkedPackageId) {
        issues.push(
          diagnostic(
            'DOCS005',
            'resources/nuget-packages.md',
            line,
            `Package link for '${visiblePackageId}' does not target a NuGet package page: '${target}'.`,
            'Link to the matching https://www.nuget.org/packages/<package-id> page.',
          ),
        );
      } else if (linkedPackageId.toLowerCase() !== visiblePackageId.toLowerCase()) {
        issues.push(
          diagnostic(
            'DOCS005',
            'resources/nuget-packages.md',
            line,
            `Displayed package '${visiblePackageId}' does not match NuGet link target '${linkedPackageId}'.`,
            'Make the displayed package ID and canonical NuGet package target identical.',
          ),
        );
      }
    }
  }
  return { documentedPackages, documentedAlpha, documentedStatus, issues };
}

export function validatePackageInventory({
  packages,
  documentedPackages,
  exclusions = { packages: {} },
}) {
  const issues = [];
  const excludedPackages = exclusions.packages ?? {};
  for (const packageId of documentedPackages) {
    const documentedPackageId = Array.isArray(packageId) ? packageId[0] : packageId;
    if (!packages.has(documentedPackageId)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'resources/nuget-packages.md',
          documentedPackages instanceof Map ? documentedPackages.get(documentedPackageId) : 1,
          `Documented package '${documentedPackageId}' has no packable source project.`,
          'Correct the package name or add the package project before documenting it.',
        ),
      );
      continue;
    }
  }
  for (const packageId of packages.keys()) {
    if (!documentedPackages.has(packageId) && !Object.hasOwn(excludedPackages, packageId)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'resources/nuget-packages.md',
          1,
          `Packable source package '${packageId}' is missing from the documented inventory.`,
          'Document the public package or add a narrow reason to src/data/package-inventory-exclusions.json.',
        ),
      );
    }
  }
  for (const [packageId, reason] of Object.entries(excludedPackages)) {
    if (!packages.has(packageId)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'src/data/package-inventory-exclusions.json',
          1,
          `Package inventory exclusion '${packageId}' has no packable source project.`,
          'Remove the stale exclusion.',
        ),
      );
    } else if (documentedPackages.has(packageId)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'src/data/package-inventory-exclusions.json',
          1,
          `Package inventory exclusion '${packageId}' is redundant because the package is documented.`,
          'Remove the redundant exclusion.',
        ),
      );
    }
    if (typeof reason !== 'string' || reason.trim().length < 20) {
      issues.push(
        diagnostic(
          'DOCS005',
          'src/data/package-inventory-exclusions.json',
          1,
          `Package inventory exclusion '${packageId}' lacks a meaningful reason.`,
          'Explain why this public package should not appear in the user-facing inventory.',
        ),
      );
    }
  }
  return issues;
}

async function validateReferenceInventories(repositoryRoot, sourceRoot, packageExclusions) {
  const issues = [];
  const packages = await collectPackageProjects(repositoryRoot);
  const packagePagePath = path.join(sourceRoot, 'resources', 'nuget-packages.md');
  const packagePage = await readFile(packagePagePath, 'utf8');
  const packageTable = parseDocumentedPackageTable(packagePage);
  issues.push(
    ...packageTable.issues,
    ...validatePackageInventory({
      packages,
      documentedPackages: packageTable.documentedPackages,
      exclusions: packageExclusions,
    }),
  );

  const streamPagePath = path.join(sourceRoot, 'streaming', 'stream-providers.md');
  const streamPage = await readFile(streamPagePath, 'utf8');
  const streamTable = parseDocumentedPackageTable(streamPage, 1, 2);
  issues.push(...streamTable.issues);
  const documentedStreams = new Map(
    [...streamTable.documentedPackages]
      .filter(([packageId]) => packageId.startsWith('Microsoft.Orleans.Streaming'))
      .map(([packageId, line]) => [
        packageId,
        { status: streamTable.documentedStatus.get(packageId), line },
      ]),
  );
  const sourceStreams = new Map(
    [...packages]
      .filter(([packageId]) => /^Microsoft\.Orleans\.Streaming(?:\.|$)/.test(packageId))
      .map(([packageId, project]) => [packageId, project.alpha ? 'Alpha' : 'Stable']),
  );
  for (const [packageId, status] of sourceStreams) {
    const documented = documentedStreams.get(packageId);
    if (!documented) {
      issues.push(
        diagnostic(
          'DOCS005',
          'streaming/stream-providers.md',
          1,
          `Stream provider package '${packageId}' is missing from the provider matrix.`,
          'Add the source-derived provider and its current status to the matrix.',
        ),
      );
    } else if (documented.status !== status) {
      issues.push(
        diagnostic(
          'DOCS005',
          'streaming/stream-providers.md',
          documented.line,
          `Stream provider '${packageId}' is '${status}' in project metadata but documented as '${documented.status}'.`,
          'Update the status to match the package VersionSuffix metadata.',
        ),
      );
    }
  }
  for (const [packageId, documented] of documentedStreams) {
    if (!sourceStreams.has(packageId)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'streaming/stream-providers.md',
          documented.line,
          `Documented stream provider '${packageId}' has no source package.`,
          'Remove the stale row or restore the provider project.',
        ),
      );
    }
  }

  const optionsPath = path.join(sourceRoot, 'host', 'configuration-guide', 'list-of-options-classes.md');
  const optionsPage = await readFile(optionsPath, 'utf8');
  if (
    !optionsPage.includes('This page is a curated starting point, not an exhaustive property catalog.') ||
    !optionsPage.includes('<xref:Orleans.Configuration>')
  ) {
    issues.push(
      diagnostic(
        'DOCS005',
        'host/configuration-guide/list-of-options-classes.md',
        1,
        'The authored options shortlist no longer identifies the generated API reference as its source of truth.',
        'Keep the list explicitly curated and link readers to the Orleans.Configuration API inventory.',
      ),
    );
  }

  const activitySource = await readFile(
    path.join(repositoryRoot, 'src', 'Orleans.Core.Abstractions', 'Diagnostics', 'ActivitySources.cs'),
    'utf8',
  );
  const sourceActivities = new Set(
    [...activitySource.matchAll(/public const string \w+ = "(Microsoft\.Orleans[^"]+)"/g)].map(
      (match) => match[1],
    ),
  );
  const observabilityPath = path.join(sourceRoot, 'host', 'monitoring', 'index.md');
  const observabilityPage = await readFile(observabilityPath, 'utf8');
  const documentedActivities = new Set(
    extractTableCodeValues(observabilityPage, 0)
      .map((item) => item.value)
      .filter((value) => value.startsWith('Microsoft.Orleans')),
  );
  for (const value of new Set([...sourceActivities, ...documentedActivities])) {
    if (!sourceActivities.has(value) || !documentedActivities.has(value)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'host/monitoring/index.md',
          1,
          `Activity source inventory drifted at '${value}'.`,
          'Keep the ActivitySources table exactly aligned with ActivitySources.cs.',
        ),
      );
    }
  }

  const instrumentSource = await readFile(
    path.join(repositoryRoot, 'src', 'Orleans.Core', 'Diagnostics', 'Metrics', 'InstrumentNames.cs'),
    'utf8',
  );
  const instruments = new Set(
    [...instrumentSource.matchAll(/=\s*"(orleans-[a-z0-9-]+)"/g)].map((match) => match[1]),
  );
  const signalsPath = path.join(sourceRoot, 'host', 'monitoring', 'signals.md');
  const signalsPage = await readFile(signalsPath, 'utf8');
  for (const match of signalsPage.matchAll(/`(orleans-[a-z0-9-]+)`/g)) {
    if (!instruments.has(match[1])) {
      issues.push(
        diagnostic(
          'DOCS005',
          'host/monitoring/signals.md',
          lineNumberAt(signalsPage, match.index),
          `Documented metric '${match[1]}' is absent from InstrumentNames.cs.`,
          'Correct the metric name or describe the signal without presenting a nonexistent instrument.',
        ),
      );
    }
  }

  const lifecycleApi = await readFile(
    path.join(repositoryRoot, 'src', 'api', 'Orleans.Core', 'Orleans.Core.cs'),
    'utf8',
  );
  const lifecycleBlock =
    /public static partial class ServiceLifecycleStage\s*\{([\s\S]*?)^\s*\}/m.exec(lifecycleApi)?.[1] ??
    '';
  const lifecycleConstants = new Map(
    [...lifecycleBlock.matchAll(/public const int (\w+) = ([^;]+);/g)].map((match) => [
      match[1],
      match[2],
    ]),
  );
  const lifecyclePath = path.join(sourceRoot, 'host', 'silo-lifecycle.md');
  const lifecyclePage = await readFile(lifecyclePath, 'utf8');
  const documentedStages = new Map(
    [...lifecyclePage.matchAll(
      /^\|\s*<xref:Orleans\.ServiceLifecycleStage\.(\w+)>\s*\|\s*`([^`]+)`\s*\|/gm,
    )].map((match) => [
      match[1],
      { value: match[2], line: lineNumberAt(lifecyclePage, match.index) },
    ]),
  );
  for (const [name, value] of lifecycleConstants) {
    const documented = documentedStages.get(name);
    if (!documented || documented.value !== value) {
      issues.push(
        diagnostic(
          'DOCS005',
          'host/silo-lifecycle.md',
          documented?.line ?? 1,
          `Lifecycle stage '${name}' is '${value}' in the public API but '${documented?.value ?? 'missing'}' in documentation.`,
          'Synchronize the complete ServiceLifecycleStage table with the checked-in API surface.',
        ),
      );
    }
  }
  for (const name of documentedStages.keys()) {
    if (!lifecycleConstants.has(name)) {
      issues.push(
        diagnostic(
          'DOCS005',
          'host/silo-lifecycle.md',
          documentedStages.get(name).line,
          `Documented lifecycle stage '${name}' is absent from the public API.`,
          'Remove the stale stage or update it to the current public constant.',
        ),
      );
    }
  }

  return issues;
}

export async function auditDocumentationSources({
  repositoryRoot,
  sourceRoot,
  tocPath,
  packageExclusions,
}) {
  const markdownFiles = await walk(sourceRoot, (file) => file.endsWith('.md'));
  const contentAudit = await auditDocumentationContent({
    repositoryRoot,
    sourceRoot,
    markdownFiles,
  });
  const { includeTargets, auditedMarkdown } = contentAudit;
  const pages = [];
  const allMarkdownPaths = [];
  const navigationExcludedPaths = [];
  for (const file of markdownFiles) {
    const relativePath = toPosix(path.relative(sourceRoot, file));
    allMarkdownPaths.push(relativePath);
    const page = auditedMarkdown.find((item) => item.file === path.resolve(file));
    if (isNavigationHidden(page.source)) {
      navigationExcludedPaths.push(relativePath);
    }
    if (
      includeTargets.has(path.resolve(file)) ||
      isDocumentationFragmentMarkdown(relativePath)
    ) {
      continue;
    }
    pages.push(page);
  }

  const tocSource = await readFile(tocPath, 'utf8');
  const tocItems = await readTocItems(tocPath);
  const issues = [...contentAudit.issues];
  issues.push(
    ...validateNavigation({
      markdownPaths: allMarkdownPaths,
      excludedPaths: markdownFiles
        .filter(
          (file) =>
            includeTargets.has(path.resolve(file)) ||
            isDocumentationFragmentMarkdown(toPosix(path.relative(sourceRoot, file))),
        )
        .map((file) => toPosix(path.relative(sourceRoot, file)))
        .concat(navigationExcludedPaths),
      tocItems,
      tocSource,
    }),
  );
  issues.push(
    ...(await validateReferenceInventories(repositoryRoot, sourceRoot, packageExclusions)),
  );
  return { issues, pages, auditedMarkdown };
}

export async function auditDocumentationContent({
  repositoryRoot,
  sourceRoot,
  markdownFiles,
  siteRoot = path.join(repositoryRoot, 'docs', 'site'),
}) {
  const includeIssues = [];
  const includeTargets = await collectIncludeTargets(markdownFiles, {
    allowedRoot: siteRoot,
    onIssue: (issue) => includeIssues.push(issue),
  });
  const files = [...new Set([...markdownFiles.map((file) => path.resolve(file)), ...includeTargets])];
  const auditedMarkdown = await Promise.all(
    files.map(async (file) => {
      const insideSourceRoot = !path.relative(sourceRoot, file).startsWith('..');
      return {
        file,
        relativePath: toPosix(
          path.relative(insideSourceRoot ? sourceRoot : repositoryRoot, file),
        ),
        source: await readFile(file, 'utf8'),
      };
    }),
  );
  const issues = includeIssues.map((issue) => {
    const insideSourceRoot = !path.relative(sourceRoot, issue.file).startsWith('..');
    return diagnostic(
      'DOCS006',
      toPosix(path.relative(insideSourceRoot ? sourceRoot : repositoryRoot, issue.file)),
      issue.line,
      issue.message,
      'Keep INCLUDE targets within docs/site, use supported syntax, and remove circular include chains.',
    );
  });
  issues.push(...auditedMarkdown.flatMap(findReleaseVersionIssues));
  issues.push(...validateCsharpFences(auditedMarkdown));
  return { issues, auditedMarkdown, includeTargets };
}
