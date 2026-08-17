import { access, readFile, readdir, realpath } from 'node:fs/promises';
import { lookup as dnsLookup } from 'node:dns/promises';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { isIP } from 'node:net';
import path from 'node:path';
import { fromMarkdown } from 'mdast-util-from-markdown';
import { parse, parseFragment } from 'parse5';
import YAML, { isMap, isScalar, isSeq, LineCounter } from 'yaml';
import {
  collectIncludeTargets,
  isDocumentationFragmentMarkdown,
} from './docfx.mjs';

const deploymentBase = '/orleans';
const contentRoute = `${deploymentBase}/docs`;
const learnContentRoot = '/dotnet/orleans';
const redirectStatuses = new Set([301, 302, 303, 307, 308]);
const transientStatuses = new Set([408, 425, 429, 500, 502, 503, 504]);
const headFallbackStatuses = new Set([405, 501]);
const headFallbackStatusesByHost = new Map([
  ['azure.microsoft.com', new Set([404])],
  ['nuget.org', new Set([404])],
  ['twitter.com', new Set([403])],
  ['www.nuget.org', new Set([404])],
]);

function toPosix(value) {
  return value.split(path.sep).join('/');
}

function isWithin(root, target) {
  const relative = path.relative(root, target);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

async function pathExists(target) {
  try {
    await access(target);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return false;
    }
    throw error;
  }
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

export async function collectXmlDocumentationExternalUrls(sourceRoot) {
  const urls = new Set();

  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      if (entry.isDirectory() && ['bin', 'obj', 'node_modules'].includes(entry.name)) continue;

      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        await visit(entryPath);
      } else if (entry.isFile() && entry.name.endsWith('.cs')) {
        const source = await readFile(entryPath, 'utf8');
        for (const match of source.matchAll(
          /<(?:see|seealso)\b[^>]*\bhref\s*=\s*["'](https?:\/\/[^"']+)["']/gi,
        )) {
          urls.add(match[1]);
        }
      }
    }
  }

  await visit(sourceRoot);
  return urls;
}

export async function collectLinkAuditDocuments({ sourceRoot, allowedRoot }) {
  const contentFiles = await walk(
    sourceRoot,
    (file) => ['.md', '.yaml', '.yml'].includes(path.extname(file).toLowerCase()),
  );
  const sourceFiles = contentFiles.filter(
    (file) => path.extname(file).toLowerCase() === '.md',
  );
  const pageCandidates = sourceFiles.filter(
    (file) => !isDocumentationFragmentMarkdown(path.relative(sourceRoot, file)),
  );
  const logicalIncludes = new Set();
  const physicalIncludes = new Set();
  const includeEdges = new Map();
  await collectIncludeTargets(pageCandidates, {
    allowedRoot,
    onTarget: (target) => {
      const source = path.resolve(target.sourcePath);
      const include = path.resolve(target.path);
      logicalIncludes.add(include);
      physicalIncludes.add(target.physicalPath);
      includeEdges.set(source, [...(includeEdges.get(source) ?? []), include]);
    },
  });
  const activePages = [];
  for (const file of pageCandidates) {
    if (!physicalIncludes.has(await realpath(file))) activePages.push(file);
  }
  const routesByFile = new Map(
    activePages.map((file) => [path.resolve(file), new Set([sourceRoute(file, sourceRoot)])]),
  );
  const pending = [...activePages.map((file) => path.resolve(file))];
  while (pending.length > 0) {
    const source = pending.shift();
    const sourceRoutes = routesByFile.get(source);
    for (const include of includeEdges.get(source) ?? []) {
      const includeRoutes = routesByFile.get(include) ?? new Set();
      const previousSize = includeRoutes.size;
      for (const route of sourceRoutes) includeRoutes.add(route);
      routesByFile.set(include, includeRoutes);
      if (includeRoutes.size !== previousSize) pending.push(include);
    }
  }
  const markdownDocuments = await Promise.all(
    [...new Set([...activePages, ...logicalIncludes])].sort().map(async (file) => ({
      file,
      source: await readFile(file, 'utf8'),
      routes: [...(routesByFile.get(path.resolve(file)) ?? [])].sort(),
      kind: 'markdown',
    })),
  );
  const yamlDocuments = await Promise.all(
    contentFiles
      .filter((file) => ['.yaml', '.yml'].includes(path.extname(file).toLowerCase()))
      .sort()
      .map(async (file) => ({
        file,
        source: await readFile(file, 'utf8'),
        kind: 'yaml',
      })),
  );
  return [...markdownDocuments, ...yamlDocuments];
}

function markdownNodes(tree, acceptedTypes) {
  const result = [];
  const pending = [tree];
  while (pending.length > 0) {
    const node = pending.pop();
    if (acceptedTypes.has(node.type)) {
      result.push(node);
    }
    if (Array.isArray(node.children)) {
      pending.push(...node.children);
    }
  }
  return result.sort((left, right) => left.position.start.offset - right.position.start.offset);
}

function htmlLinks(node) {
  const links = [];
  const tree = parseFragment(node.value, { sourceCodeLocationInfo: true });
  const protectedTags = new Set(['code', 'pre', 'script', 'style']);
  const urlAttributes = new Map([
    ['a', ['href']],
    ['area', ['href']],
    ['audio', ['src']],
    ['embed', ['src']],
    ['form', ['action']],
    ['iframe', ['src']],
    ['img', ['src', 'srcset']],
    ['input', ['src']],
    ['link', ['href']],
    ['object', ['data']],
    ['source', ['src', 'srcset']],
    ['track', ['src']],
    ['video', ['poster', 'src']],
  ]);
  function visit(current, protectedContent) {
    const nextProtected = protectedContent || protectedTags.has(current.tagName);
    if (!nextProtected) {
      const attrs = attributes(current);
      for (const attribute of urlAttributes.get(current.tagName) ?? []) {
        const value = attrs.get(attribute);
        if (!value) continue;
        const urls =
          attribute === 'srcset'
            ? value
                .split(',')
                .map((candidate) => candidate.trim().split(/\s+/, 1)[0])
                .filter(Boolean)
            : [value];
        for (const url of urls) {
          links.push({
            url,
            line:
              node.position.start.line +
              (current.sourceCodeLocation?.startLine ?? 1) -
              1,
          });
        }
      }
    }
    for (const child of current.childNodes ?? []) visit(child, nextProtected);
  }
  visit(tree, false);
  return links;
}

export function collectMarkdownLinkReferences({ source, file, sourceRoot }) {
  const tree = fromMarkdown(source);
  const sourceLines = source.replaceAll('\r\n', '\n').split('\n');
  const relativeFile = toPosix(
    path.relative(isWithin(sourceRoot, file) ? sourceRoot : path.dirname(sourceRoot), file),
  );
  const definitions = new Map(
    markdownNodes(tree, new Set(['definition'])).map((node) => [node.identifier, node.url]),
  );
  const references = [];
  for (const node of markdownNodes(
    tree,
    new Set(['link', 'linkReference', 'image', 'imageReference', 'html']),
  )) {
    if (
      /^\s*(?:\[!INCLUDE\b|:::code\b)/.test(
        sourceLines[node.position.start.line - 1] ?? '',
      )
    ) {
      continue;
    }
    if (node.type === 'html') {
      references.push(
        ...htmlLinks(node).map((link) => ({
          ...link,
          file,
          relativeFile,
        })),
      );
      continue;
    }
    const url =
      node.type === 'link' || node.type === 'image'
        ? node.url
        : definitions.get(node.identifier);
    if (url) {
      references.push({
        url,
        line: node.position.start.line,
        file,
        relativeFile,
      });
    }
  }
  return references;
}

function yamlLine(lineCounter, node) {
  return node?.range ? lineCounter.linePos(node.range[0]).line : 1;
}

export function collectYamlLinkReferences({ source, file, sourceRoot }) {
  const lineCounter = new LineCounter();
  const document = YAML.parseDocument(source, { lineCounter });
  if (document.errors.length > 0) {
    throw document.errors[0];
  }
  const references = [];
  const urlFields = new Set(['homepage', 'href', 'url']);
  const markdownFields = new Set(['description', 'footer', 'summary']);
  const relativeFile = toPosix(path.relative(sourceRoot, file));
  function visit(node) {
    if (isMap(node)) {
      for (const pair of node.items) {
        const key = isScalar(pair.key) ? String(pair.key.value) : '';
        if (isScalar(pair.value) && typeof pair.value.value === 'string') {
          if (urlFields.has(key) && /^https?:\/\//i.test(pair.value.value)) {
            references.push({
              url: pair.value.value,
              line: yamlLine(lineCounter, pair.value),
              file,
              relativeFile,
            });
          } else if (markdownFields.has(key)) {
            const startLine = yamlLine(lineCounter, pair.value);
            references.push(
              ...collectMarkdownLinkReferences({
                source: pair.value.value,
                file,
                sourceRoot,
              }).map((reference) => ({
                ...reference,
                line: startLine + reference.line - 1,
              })),
            );
          }
        }
        visit(pair.value);
      }
    } else if (isSeq(node)) {
      for (const item of node.items) visit(item);
    }
  }
  visit(document.contents);
  return references;
}

function learnUrlForRelative(reference, sourceFile, sourceRoot) {
  const relativeSource = toPosix(path.relative(sourceRoot, sourceFile));
  const sourceDirectory = path.posix.dirname(relativeSource);
  const base = new URL(
    `https://learn.microsoft.com${learnContentRoot}/${sourceDirectory === '.' ? '' : `${sourceDirectory}/`}`,
  );
  const result = new URL(reference, base);
  result.pathname = result.pathname.replace(/\.(?:md|markdown|mdown|mkdn|mkd|mdwn)$/i, '');
  return result.href;
}

function localSourceTarget(reference, sourceFile) {
  const pathname = reference.split(/[?#]/, 1)[0];
  return pathname ? path.resolve(path.dirname(sourceFile), decodeURIComponent(pathname)) : sourceFile;
}

function routeFromMarkdownRelative(relativeFile) {
  let route = relativeFile.replace(/\.(?:md|markdown|mdown|mkdn|mkd|mdwn|yml)$/i, '');
  if (path.posix.basename(route).toLowerCase() === 'index') {
    route = path.posix.dirname(route);
  }
  route = route === '.' ? '' : route.replace(/^\/+|\/+$/g, '');
  return `${contentRoute}/${route}${route ? '/' : ''}`.replace(/\/{2,}/g, '/');
}

function sourceRoute(file, sourceRoot) {
  const relative = toPosix(path.relative(sourceRoot, file));
  return isWithin(sourceRoot, file) ? routeFromMarkdownRelative(relative) : undefined;
}

function renderedInternalTarget(reference, sourceRoot, route) {
  const currentRoute = route ?? sourceRoute(reference.file, sourceRoot);
  if (!currentRoute) return undefined;
  const raw = reference.url.trim();
  let rendered = raw;
  if (raw === learnContentRoot || raw.startsWith(`${learnContentRoot}/`)) {
    rendered = `${contentRoute}${raw.slice(learnContentRoot.length) || '/'}`;
  } else if (!raw.startsWith('/') && !raw.startsWith('#')) {
    const match = /^([^?#]+)([?#].*)?$/.exec(raw);
    if (match && /\.(?:md|markdown|mdown|mkdn|mkd|mdwn|yml)$/i.test(match[1])) {
      const sourceRelative = toPosix(path.relative(sourceRoot, reference.file));
      const targetRelative = path.posix.normalize(
        path.posix.join(path.posix.dirname(sourceRelative), match[1]),
      );
      rendered = `${routeFromMarkdownRelative(targetRelative)}${match[2] ?? ''}`;
    }
  }
  try {
    const url = new URL(rendered, `https://dotnet.github.io${currentRoute}`);
    return url.origin === 'https://dotnet.github.io'
      ? { currentRoute, target: `${url.pathname}${url.search}${url.hash}` }
      : undefined;
  } catch {
    return undefined;
  }
}

function addExternal(externalTargets, url, reference) {
  let normalized;
  try {
    normalized = new URL(url);
  } catch {
    return {
      rule: 'LINK001',
      file: reference.relativeFile,
      line: reference.line,
      message: `Malformed external URL '${url}'.`,
      remediation: 'Use a valid absolute http(s) URL.',
    };
  }
  if (!['http:', 'https:'].includes(normalized.protocol)) {
    return {
      rule: 'LINK001',
      file: reference.relativeFile,
      line: reference.line,
      message: `Invalid external URL protocol in '${url}'.`,
      remediation: 'Use http(s), a supported non-web scheme, or a valid internal link.',
    };
  }
  normalized.hash = '';
  const key = normalized.href;
  const entries = externalTargets.get(key) ?? [];
  entries.push(reference);
  externalTargets.set(key, entries);
  return undefined;
}

export async function auditSourceLinks({ documents, sourceRoot }) {
  const issues = [];
  const externalTargets = new Map();
  const internalProvenance = new Map();
  const references = documents.flatMap((document) =>
    (document.kind === 'yaml'
      ? collectYamlLinkReferences({
          source: document.source,
          file: document.file,
          sourceRoot,
        })
      : collectMarkdownLinkReferences({
          source: document.source,
          file: document.file,
          sourceRoot,
        })
    ).map((reference) => ({ ...reference, routes: document.routes })),
  );
  function addInternalProvenance(reference) {
    const routes = reference.routes?.length ? reference.routes : [undefined];
    for (const route of routes) {
      const internal = renderedInternalTarget(reference, sourceRoot, route);
      if (!internal) continue;
      const key = `${internal.currentRoute}\0${internal.target}`;
      internalProvenance.set(key, [...(internalProvenance.get(key) ?? []), reference]);
    }
  }

  for (const reference of references) {
    const raw = reference.url.trim();
    if (
      raw.length === 0 ||
      raw.startsWith('#') ||
      /^(?:mailto|tel|sms|xref):/i.test(raw)
    ) {
      addInternalProvenance(reference);
      continue;
    }
    if (/^https?:\/\//i.test(raw)) {
      let parsed;
      try {
        parsed = new URL(raw);
      } catch {
        const issue = addExternal(externalTargets, raw, reference);
        if (issue) issues.push(issue);
        continue;
      }
      if (
        parsed.hostname.toLowerCase() === 'dotnet.github.io' &&
        (parsed.pathname === deploymentBase ||
          parsed.pathname.startsWith(`${deploymentBase}/`))
      ) {
        addInternalProvenance(reference);
        continue;
      }
      const issue = addExternal(externalTargets, raw, reference);
      if (issue) issues.push(issue);
      continue;
    }
    if (/^[a-z][a-z\d+.-]*:/i.test(raw) || raw.startsWith('//')) {
      issues.push({
        rule: 'LINK001',
        file: reference.relativeFile,
        line: reference.line,
        message: `Unsupported URL scheme in '${raw}'.`,
        remediation:
          'Use http(s), mailto, tel, sms, xref, or a valid internal link.',
      });
      continue;
    }
    if (raw.startsWith('/')) {
      if (
        raw === deploymentBase ||
        raw.startsWith(`${deploymentBase}/`) ||
        raw === learnContentRoot ||
        raw.startsWith(`${learnContentRoot}/`)
      ) {
        addInternalProvenance(reference);
        continue;
      }
      const learnUrl = new URL(raw, 'https://learn.microsoft.com');
      learnUrl.pathname = learnUrl.pathname.replace(
        /\.(?:md|markdown|mdown|mkdn|mkd|mdwn)$/i,
        '',
      );
      issues.push({
        rule: 'LINK001',
        file: reference.relativeFile,
        line: reference.line,
        message: `Migrated Learn root-relative link '${raw}' is outside the Orleans site route space.`,
        remediation: `Replace it with '${learnUrl.href}'.`,
      });
      continue;
    }

    let target;
    try {
      target = localSourceTarget(raw, reference.file);
    } catch {
      issues.push({
        rule: 'LINK001',
        file: reference.relativeFile,
        line: reference.line,
        message: `Malformed encoded relative link '${raw}'.`,
        remediation: 'Correct the encoded path.',
      });
      continue;
    }
    const pathname = raw.split(/[?#]/, 1)[0];
    addInternalProvenance(reference);
    const markdownTarget = /\.(?:md|markdown|mdown|mkdn|mkd|mdwn|yml)$/i.test(pathname);
    if (!isWithin(sourceRoot, target)) {
      const learnUrl = learnUrlForRelative(raw, reference.file, sourceRoot);
      issues.push({
        rule: 'LINK001',
        file: reference.relativeFile,
        line: reference.line,
        message: `Migrated Learn-relative link '${raw}' resolves outside the Orleans content root.`,
        remediation: `Replace it with '${learnUrl}'.`,
      });
    } else if (markdownTarget && !(await pathExists(target))) {
      issues.push({
        rule: 'LINK001',
        file: reference.relativeFile,
        line: reference.line,
        message: `Relative documentation link '${raw}' targets a missing Orleans source page.`,
        remediation: 'Correct the relative path or use the canonical absolute Microsoft Learn URL.',
      });
    }
  }
  return { issues, externalTargets, internalProvenance, references };
}

function routeForOutputFile(relativeFile) {
  if (relativeFile === 'index.html') return `${deploymentBase}/`;
  if (relativeFile.endsWith('/index.html')) {
    return `${deploymentBase}/${relativeFile.slice(0, -'index.html'.length)}`;
  }
  return `${deploymentBase}/${relativeFile}`;
}

function attributes(node) {
  return new Map((node.attrs ?? []).map((attribute) => [attribute.name, attribute.value]));
}

function traverse(node, callback) {
  callback(node);
  for (const child of node.childNodes ?? []) {
    traverse(child, callback);
  }
  if (node.content) {
    traverse(node.content, callback);
  }
}

function renderedDocument(html) {
  const tree = parse(html);
  const ids = new Set();
  const links = [];
  const urlAttributes = new Map([
    ['a', ['href']],
    ['area', ['href']],
    ['audio', ['src']],
    ['embed', ['src']],
    ['form', ['action']],
    ['iframe', ['src']],
    ['img', ['src', 'srcset']],
    ['input', ['src']],
    ['link', ['href']],
    ['object', ['data']],
    ['script', ['src']],
    ['source', ['src', 'srcset']],
    ['track', ['src']],
    ['video', ['poster', 'src']],
  ]);
  function addUrl(value, kind, navigational = false) {
    if (!value) return;
    if (kind === 'srcset') {
      for (const candidate of value.split(',')) {
        const url = candidate.trim().split(/\s+/, 1)[0];
        if (url) links.push({ url, navigational: false });
      }
      return;
    }
    links.push({ url: value, navigational });
  }
  traverse(tree, (node) => {
    const attrs = attributes(node);
    if (attrs.has('id')) ids.add(attrs.get('id'));
    if (node.tagName === 'a' && attrs.has('name')) ids.add(attrs.get('name'));
    for (const attribute of urlAttributes.get(node.tagName) ?? []) {
      if (
        node.tagName === 'link' &&
        attribute === 'href' &&
        attrs.get('rel')?.toLowerCase() === 'canonical'
      ) {
        continue;
      }
      addUrl(
        attrs.get(attribute),
        attribute,
        attribute === 'href' && (node.tagName === 'a' || node.tagName === 'area'),
      );
    }
    if (
      node.tagName === 'meta' &&
      attrs.get('http-equiv')?.toLowerCase() === 'refresh' &&
      attrs.has('content')
    ) {
      const refresh = /^\s*\d+(?:\.\d+)?\s*;\s*url\s*=\s*(.*?)\s*$/i.exec(
        attrs.get('content'),
      );
      if (refresh) {
        addUrl(refresh[1].replace(/^(["'])(.*)\1$/, '$2'), 'refresh', true);
      }
    }
  });
  return { ids, links };
}

function normalizeInternalPath(pathname) {
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    throw new Error(`Malformed encoded path '${pathname}'.`);
  }
  const normalized = path.posix.normalize(decoded);
  if (normalized !== deploymentBase && !normalized.startsWith(`${deploymentBase}/`)) {
    throw new Error(`Internal link '${pathname}' escapes the '${deploymentBase}/' base path.`);
  }
  return normalized;
}

function outputCandidates(pathname) {
  const normalized = normalizeInternalPath(pathname);
  const relative = normalized === deploymentBase ? '' : normalized.slice(`${deploymentBase}/`.length);
  if (relative.length === 0 || normalized.endsWith('/')) {
    return [path.posix.join(relative, 'index.html')];
  }
  return [relative, path.posix.join(relative, 'index.html')];
}

function repositorySourceTarget(url, repositoryRoot) {
  if (!repositoryRoot || url.hostname.toLowerCase() !== 'github.com' || url.search) {
    return undefined;
  }
  const match =
    /^\/dotnet\/orleans\/blob\/(?:main|[a-f\d]{40})\/(.+)$/i.exec(url.pathname);
  if (!match || (url.hash && !/^#L\d+(?:-L\d+)?$/i.test(url.hash))) {
    return undefined;
  }
  let relative;
  try {
    relative = decodeURIComponent(match[1]);
  } catch {
    return { error: `malformed encoded repository path in '${url.href}'` };
  }
  const target = path.resolve(repositoryRoot, relative.replaceAll('/', path.sep));
  if (!isWithin(repositoryRoot, target)) {
    return { error: `repository source link '${url.href}' escapes the repository root` };
  }
  const lineMatch = /^#L(\d+)(?:-L(\d+))?$/i.exec(url.hash);
  return {
    target,
    lastLine: lineMatch ? Number(lineMatch[2] ?? lineMatch[1]) : undefined,
  };
}

export async function auditRenderedInternalLinks({
  distRoot,
  repositoryRoot,
  internalProvenance = new Map(),
  externalTargets,
}) {
  const files = await walk(distRoot);
  const relativeFiles = new Set(files.map((file) => toPosix(path.relative(distRoot, file))));
  const htmlFiles = files.filter((file) => file.endsWith('.html'));
  const documentCache = new Map();
  async function getDocument(relativeFile) {
    if (!documentCache.has(relativeFile)) {
      documentCache.set(
        relativeFile,
        renderedDocument(await readFile(path.join(distRoot, relativeFile), 'utf8')),
      );
    }
    return documentCache.get(relativeFile);
  }
  const issues = [];
  const sourceLineCounts = new Map();
  async function sourceLineCount(target) {
    if (!sourceLineCounts.has(target)) {
      sourceLineCounts.set(
        target,
        pathExists(target).then(async (exists) =>
          exists ? (await readFile(target, 'utf8')).split(/\r?\n/).length : 0,
        ),
      );
    }
    return sourceLineCounts.get(target);
  }
  function provenance(route, targetUrl) {
    const key = `${route}\0${targetUrl.pathname}${targetUrl.search}${targetUrl.hash}`;
    const references = internalProvenance.get(key);
    return references?.length
      ? `${references[0].relativeFile}:${references[0].line}`
      : route;
  }
  for (const file of htmlFiles) {
    const relativeFile = toPosix(path.relative(distRoot, file));
    const route = routeForOutputFile(relativeFile);
    const document = await getDocument(relativeFile);
    for (const link of document.links) {
      const href = link.url;
      if (href.length === 0 || /^(?:mailto|tel|sms):/i.test(href)) {
        continue;
      }
      let targetUrl;
      try {
        targetUrl = new URL(href, `https://dotnet.github.io${route}`);
      } catch {
        issues.push(`${route}: malformed href '${href}'.`);
        continue;
      }
      if (targetUrl.protocol === 'data:' && !link.navigational) {
        continue;
      }
      if (!['http:', 'https:'].includes(targetUrl.protocol)) {
        issues.push(
          `${route}: unsupported URL protocol '${targetUrl.protocol}' in '${href}'.`,
        );
        continue;
      }
      if (
        targetUrl.origin !== 'https://dotnet.github.io' &&
        targetUrl.origin !== 'https://docs.invalid'
      ) {
        const sourceTarget = repositorySourceTarget(targetUrl, repositoryRoot);
        if (sourceTarget) {
          if (sourceTarget.error) {
            issues.push(`${route}: ${sourceTarget.error}.`);
          } else {
            const lines = await sourceLineCount(sourceTarget.target);
            if (lines === 0) {
              issues.push(
                `${route}: repository source link '${href}' targets a missing file.`,
              );
            } else if (sourceTarget.lastLine && sourceTarget.lastLine > lines) {
              issues.push(
                `${route}: repository source link '${href}' targets line ${sourceTarget.lastLine}, but the file has ${lines} lines.`,
              );
            }
          }
          continue;
        }
        if (['http:', 'https:'].includes(targetUrl.protocol) && externalTargets) {
          const normalized = new URL(targetUrl);
          normalized.hash = '';
          if (!externalTargets.has(normalized.href)) {
            externalTargets.set(normalized.href, [
              { relativeFile: route, line: 1, rendered: true },
            ]);
          }
        }
        continue;
      }
      let candidates;
      try {
        candidates = outputCandidates(targetUrl.pathname);
      } catch (error) {
        issues.push(`${route}: ${error.message} Href: '${href}'.`);
        continue;
      }
      const targetFile = candidates.find((candidate) => relativeFiles.has(candidate));
      if (!targetFile) {
        issues.push(
          `${provenance(route, targetUrl)}: href '${href}' targets a missing rendered path.`,
        );
        continue;
      }
      if (link.navigational && targetUrl.hash && targetFile.endsWith('.html')) {
        let fragment;
        try {
          fragment = decodeURIComponent(targetUrl.hash.slice(1));
        } catch {
          issues.push(`${route}: href '${href}' has a malformed encoded fragment.`);
          continue;
        }
        if (fragment && !(await getDocument(targetFile)).ids.has(fragment)) {
          issues.push(
            `${provenance(route, targetUrl)}: href '${href}' targets missing anchor '#${fragment}'.`,
          );
        }
      }
    }
  }
  return issues;
}

function referenceSummary(references) {
  return references
    .slice(0, 5)
    .map((reference) => `${reference.relativeFile}:${reference.line}`)
    .join(', ');
}

async function delay(milliseconds) {
  await new Promise((resolve) => setTimeout(resolve, milliseconds));
}

const blockedIpv4Ranges = [
  ['0.0.0.0', 8],
  ['10.0.0.0', 8],
  ['100.64.0.0', 10],
  ['127.0.0.0', 8],
  ['169.254.0.0', 16],
  ['172.16.0.0', 12],
  ['192.0.0.0', 24],
  ['192.0.2.0', 24],
  ['192.88.99.0', 24],
  ['192.168.0.0', 16],
  ['198.18.0.0', 15],
  ['198.51.100.0', 24],
  ['203.0.113.0', 24],
  ['224.0.0.0', 4],
  ['240.0.0.0', 4],
];

function ipv4Number(address) {
  return address
    .split('.')
    .reduce((value, octet) => (value * 256 + Number(octet)) >>> 0, 0);
}

function ipv4InCidr(address, network, prefix) {
  const mask = prefix === 0 ? 0 : (0xffffffff << (32 - prefix)) >>> 0;
  return (ipv4Number(address) & mask) === (ipv4Number(network) & mask);
}

function parseIpv6(address) {
  const zoneIndex = address.indexOf('%');
  const value = (zoneIndex >= 0 ? address.slice(0, zoneIndex) : address).toLowerCase();
  const halves = value.split('::');
  if (halves.length > 2) return undefined;
  function groups(part) {
    if (!part) return [];
    const result = part.split(':');
    const last = result.at(-1);
    if (last?.includes('.')) {
      if (isIP(last) !== 4) return undefined;
      const ipv4 = ipv4Number(last);
      result.splice(-1, 1, ((ipv4 >>> 16) & 0xffff).toString(16), (ipv4 & 0xffff).toString(16));
    }
    if (result.some((group) => !/^[\da-f]{1,4}$/.test(group))) return undefined;
    return result.map((group) => Number.parseInt(group, 16));
  }
  const left = groups(halves[0]);
  const right = groups(halves[1]);
  if (!left || !right) return undefined;
  const missing = 8 - left.length - right.length;
  if ((halves.length === 1 && missing !== 0) || (halves.length === 2 && missing < 1)) {
    return undefined;
  }
  return [...left, ...Array.from({ length: missing }, () => 0), ...right];
}

function ipv6Prefix(address, prefixGroups, prefixBits) {
  const groups = parseIpv6(address);
  if (!groups) return false;
  const completeGroups = Math.floor(prefixBits / 16);
  for (let index = 0; index < completeGroups; index += 1) {
    if (groups[index] !== prefixGroups[index]) return false;
  }
  const remainingBits = prefixBits % 16;
  if (remainingBits === 0) return true;
  const mask = (0xffff << (16 - remainingBits)) & 0xffff;
  return (groups[completeGroups] & mask) === (prefixGroups[completeGroups] & mask);
}

export function isPublicInternetAddress(address) {
  const family = isIP(address);
  if (family === 4) {
    return !blockedIpv4Ranges.some(([network, prefix]) =>
      ipv4InCidr(address, network, prefix),
    );
  }
  if (family !== 6) return false;
  const groups = parseIpv6(address);
  if (!groups) return false;
  if (groups.slice(0, 5).every((group) => group === 0) && groups[5] === 0xffff) {
    const mapped = `${groups[6] >>> 8}.${groups[6] & 0xff}.${groups[7] >>> 8}.${groups[7] & 0xff}`;
    return isPublicInternetAddress(mapped);
  }
  const blocked = [
    ['::', 128],
    ['::1', 128],
    ['64:ff9b::', 96],
    ['64:ff9b:1::', 48],
    ['100::', 64],
    ['2001::', 23],
    ['2001:db8::', 32],
    ['2002::', 16],
    ['3fff::', 20],
    ['fc00::', 7],
    ['fe80::', 10],
    ['fec0::', 10],
    ['ff00::', 8],
  ];
  return !blocked.some(([network, prefix]) =>
    ipv6Prefix(address, parseIpv6(network), prefix),
  );
}

function externalDestinationError(message) {
  const error = new Error(message);
  error.code = 'ERR_EXTERNAL_DESTINATION';
  return error;
}

async function resolveExternalDestination(
  url,
  {
    lookupImpl = dnsLookup,
  } = {},
) {
  if (!['http:', 'https:'].includes(url.protocol)) {
    throw externalDestinationError(`External URL has unsupported protocol '${url.protocol}'.`);
  }
  if (url.username || url.password) {
    throw externalDestinationError(`External URL '${url.href}' must not contain credentials.`);
  }
  const expectedPort = url.protocol === 'https:' ? '443' : '80';
  if (url.port && url.port !== expectedPort) {
    throw externalDestinationError(
      `External URL '${url.href}' uses disallowed port '${url.port}'.`,
    );
  }

  const hostname = url.hostname.replace(/^\[(.*)\]$/, '$1');
  const literalFamily = isIP(hostname);
  let addresses;
  if (literalFamily) {
    addresses = [{ address: hostname, family: literalFamily }];
  } else {
    addresses = await lookupImpl(hostname, { all: true, verbatim: true });
  }
  if (!Array.isArray(addresses) || addresses.length === 0) {
    throw externalDestinationError(`External host '${hostname}' resolved to no addresses.`);
  }
  const invalid = addresses.find(
    ({ address }) => !isPublicInternetAddress(address),
  );
  if (invalid) {
    throw externalDestinationError(
      `External host '${hostname}' resolves to non-public address '${invalid.address}'.`,
    );
  }
  return { hostname, ...addresses[0] };
}

export function createPinnedRequestOptions(url, { method, destination }) {
  return {
    method,
    agent: false,
    autoSelectFamily: false,
    headers: {
      Host: url.host,
      ...(method === 'GET' ? { Range: 'bytes=0-0' } : {}),
    },
    lookup: (_hostname, options, callback) => {
      if (options?.all) {
        callback(null, [
          { address: destination.address, family: destination.family },
        ]);
      } else {
        callback(null, destination.address, destination.family);
      }
    },
    rejectUnauthorized: true,
    servername: isIP(destination.hostname) ? undefined : destination.hostname,
  };
}

function pinnedHttpRequest(
  url,
  {
    method,
    timeoutMs,
    destination,
  },
) {
  return new Promise((resolve, reject) => {
    const request = (url.protocol === 'https:' ? httpsRequest : httpRequest)(
      url,
      createPinnedRequestOptions(url, { method, destination }),
      (response) => {
        response.destroy();
        resolve({
          status: response.statusCode ?? 0,
          headers: {
            get(name) {
              const value = response.headers[name.toLowerCase()];
              return Array.isArray(value) ? value[0] : value ?? null;
            },
          },
        });
      },
    );
    request.setTimeout(timeoutMs, () => {
      const error = new Error(`Request to '${url.href}' timed out after ${timeoutMs}ms.`);
      error.name = 'TimeoutError';
      error.code = 'ETIMEDOUT';
      request.destroy(error);
    });
    request.on('error', reject);
    request.end();
  });
}

async function requestWithRedirects(url, {
  method,
  requestImpl,
  lookupImpl,
  timeoutMs,
  maxRedirects,
}) {
  let current = new URL(url);
  const visited = new Set();
  for (let redirect = 0; redirect <= maxRedirects; redirect += 1) {
    if (visited.has(current.href)) {
      throw new Error(`Redirect loop detected at '${current.href}'.`);
    }
    visited.add(current.href);
    let response;
    try {
      const destination = await resolveExternalDestination(current, {
        lookupImpl,
      });
      response = await requestImpl(current, {
        method,
        timeoutMs,
        destination,
      });
    } catch (error) {
      const code = error.cause?.code ?? error.code;
      error.networkCode = code;
      error.transient =
        error.name === 'TimeoutError' ||
        error.name === 'AbortError' ||
        new Set([
          'EAI_AGAIN',
          'ECONNABORTED',
          'ECONNRESET',
          'ETIMEDOUT',
          'UND_ERR_BODY_TIMEOUT',
          'UND_ERR_CONNECT_TIMEOUT',
          'UND_ERR_HEADERS_TIMEOUT',
          'UND_ERR_SOCKET',
        ]).has(code);
      throw error;
    }
    if (!redirectStatuses.has(response.status)) {
      return { response, finalUrl: current.href };
    }
    const location = response.headers.get('location');
    if (!location) {
      throw new Error(`Redirect ${response.status} from '${current.href}' has no Location header.`);
    }
    const next = new URL(location, current);
    if (!['http:', 'https:'].includes(next.protocol)) {
      throw new Error(`Redirect from '${current.href}' has invalid destination '${next.href}'.`);
    }
    current = next;
  }
  throw new Error(`Too many redirects from '${url}'.`);
}

async function probeOnce(url, options) {
  const head = await requestWithRedirects(url, { ...options, method: 'HEAD' });
  if (head.response.status >= 200 && head.response.status < 400) {
    return head;
  }
  const hostFallbacks = headFallbackStatusesByHost.get(
    new URL(url).hostname.toLowerCase(),
  );
  if (
    headFallbackStatuses.has(head.response.status) ||
    hostFallbacks?.has(head.response.status)
  ) {
    return requestWithRedirects(url, { ...options, method: 'GET' });
  }
  return head;
}

export async function probeExternalTargets({
  externalTargets,
  allowlist = { urls: {} },
  allowlistReferences = new Set(),
  requestImpl = pinnedHttpRequest,
  lookupImpl = dnsLookup,
  concurrency = 8,
  timeoutMs = 10_000,
  retries = 2,
  maxRedirects = 8,
  maxTargets = 5_000,
}) {
  const failures = [];
  const warnings = [];
  const validAllowlistUrls = new Set();
  for (const [url, reason] of Object.entries(allowlist.urls ?? {})) {
    let parsed;
    try {
      parsed = new URL(url);
      if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error();
    } catch {
      failures.push(`External link allowlist contains malformed URL '${url}'.`);
      continue;
    }
    if (typeof reason !== 'string' || reason.trim().length < 20) {
      failures.push(`External link allowlist entry '${url}' lacks a meaningful reason.`);
    }
    if (!externalTargets.has(url) && !allowlistReferences.has(url)) {
      failures.push(`External link allowlist entry '${url}' is stale and no longer referenced.`);
    }
    try {
      await resolveExternalDestination(parsed, { lookupImpl });
      validAllowlistUrls.add(url);
    } catch (error) {
      failures.push(
        `External link allowlist entry '${url}' has an unsafe or unresolved destination: ${error.message}`,
      );
    }
  }
  const entries = [...externalTargets.entries()].filter(
    ([url]) =>
      !Object.hasOwn(allowlist.urls ?? {}, url) || validAllowlistUrls.has(url),
  );
  if (entries.length > maxTargets) {
    failures.push(
      `External link audit discovered ${entries.length} targets, exceeding the request cap of ${maxTargets}.`,
    );
    return { failures, warnings, probed: 0 };
  }
  let next = 0;
  async function worker() {
    while (next < entries.length) {
      const [url, references] = entries[next++];
      let result;
      let error;
      for (let attempt = 0; attempt <= retries; attempt += 1) {
        try {
          result = await probeOnce(url, {
            requestImpl,
            lookupImpl,
            timeoutMs,
            maxRedirects,
          });
          error = undefined;
          if (!transientStatuses.has(result.response.status)) break;
        } catch (caught) {
          error = caught;
          if (!caught.transient) break;
        }
        if (attempt < retries) await delay(100 * 2 ** attempt);
      }
      const provenance = referenceSummary(references);
      const allowlistReason = allowlist.urls?.[url];
      if (error) {
        const message = `${url} (${provenance}): ${error.message}${error.networkCode ? ` (${error.networkCode})` : ''}`;
        if (allowlistReason && error.transient) {
          warnings.push(`Allowlisted '${url}' could not be probed: ${message}. Reason: ${allowlistReason}`);
        } else if (error.transient) warnings.push(`Transient external link failure: ${message}`);
        else failures.push(message);
        continue;
      }
      const status = result.response.status;
      if (allowlistReason) {
        if (status >= 200 && status < 400) {
          failures.push(
            `External link allowlist entry '${url}' is stale because the target now returns ${status}; remove the entry.`,
          );
        } else {
          warnings.push(`Allowlisted '${url}' returned ${status}: ${allowlistReason}`);
        }
      } else if (status === 404 || status === 410) {
        failures.push(`${url} (${provenance}): returned ${status}.`);
      } else if (status >= 400 && !transientStatuses.has(status)) {
        failures.push(`${url} (${provenance}): returned ${status}; add a reasoned exact-URL allowlist entry only if the target cannot be probed reliably.`);
      } else if (transientStatuses.has(status)) {
        warnings.push(`Transient external status ${status}: ${url} (${provenance}).`);
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, entries.length || 1) }, worker));
  return { failures, warnings, probed: entries.length };
}
