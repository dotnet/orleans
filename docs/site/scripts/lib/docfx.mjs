import { access, readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';
import { collectIncludeTargets as collectIncludeTargetsWithoutYaml } from './include-closure.mjs';
import {
  lineOverlapsRanges,
  markdownBlockquoteLineRanges,
  markdownDirectiveProtectedLineRanges,
  markdownLiteralLineRanges,
} from './markdown-ranges.mjs';

const markdownExtensions = new Set(['.md', '.markdown', '.mdown', '.mkdn', '.mkd', '.mdwn']);
const directiveAttributePattern = /([\w-]+)="([^"]*)"/g;
const learnDocsPrefix = '/dotnet/orleans';
const contentRoot = '/docs';
const deploymentBase = '/orleans';
const siteBase = `${deploymentBase}${contentRoot}`;

export function isSnippetSupportMarkdown(relativePath) {
  const segments = relativePath.split(/[\\/]/);
  return (
    path.basename(relativePath).toLowerCase() === 'readme.md' &&
    segments.some((segment) => /^snippets(?:-v3)?$/i.test(segment))
  );
}

export function isDocumentationFragmentMarkdown(relativePath) {
  return (
    relativePath.split(/[\\/]/).some((segment) => segment.toLowerCase() === 'includes') ||
    isSnippetSupportMarkdown(relativePath)
  );
}

function toPath(value) {
  return value instanceof URL ? fileURLToPath(value) : path.resolve(value);
}

function normalizeFenceLanguages(source) {
  const aliases = {
    azurecli: 'shell',
    azuredeveloper: 'shell',
    dotnetcli: 'shell',
    output: 'text',
  };
  return source.replace(
    /^(\s*(?:`{3,}|~{3,}))([A-Za-z][\w-]*)/gm,
    (_match, marker, language) => `${marker}${aliases[language.toLowerCase()] ?? language}`,
  );
}

function updateFence(fence, line) {
  const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
  if (!marker) {
    return fence;
  }
  if (!fence) {
    return { character: marker[0], length: marker.length };
  }
  if (marker[0] === fence.character && marker.length >= fence.length) {
    return undefined;
  }
  return fence;
}

function convertTabs(source, sourcePath) {
  const lines = source.split('\n');
  const output = [];
  const tabPattern = /^(\s*)#{1,6}\s+\[([^\]]+)\]\(#tab\/([^)]+)\)\s*$/;
  const terminatorPattern = /^(\s*)(?:---|\*\*\*)\s*$/;
  let converted = false;
  let fence;

  for (let index = 0; index < lines.length; ) {
    const line = lines[index];
    const tab = fence ? undefined : tabPattern.exec(line);
    if (!tab) {
      output.push(line);
      fence = updateFence(fence, line);
      index += 1;
      continue;
    }

    const indent = tab[1];
    const items = [];
    let terminated = false;
    while (index < lines.length) {
      const heading = tabPattern.exec(lines[index]);
      if (!heading || heading[1] !== indent) {
        break;
      }

      const content = [];
      const label = heading[2];
      index += 1;
      let contentFence;
      while (index < lines.length) {
        const current = lines[index];
        const nextHeading = contentFence ? undefined : tabPattern.exec(current);
        if (nextHeading?.[1] === indent) {
          break;
        }

        const terminator = contentFence ? undefined : terminatorPattern.exec(current);
        if (terminator?.[1] === indent) {
          terminated = true;
          index += 1;
          break;
        }

        content.push(current);
        contentFence = updateFence(contentFence, current);
        index += 1;
      }

      while (content[0] === '') {
        content.shift();
      }
      while (content.at(-1) === '') {
        content.pop();
      }
      items.push({ label, content });
      if (terminated) {
        break;
      }
    }

    if (!terminated) {
      throw new Error(`Unclosed tab group in ${sourcePath}.`);
    }

    converted = true;
    output.push(`${indent}<Tabs syncKey="docfx-tabs">`);
    for (const item of items) {
      output.push(`${indent}<TabItem label="${escapeHtml(item.label)}">`);
      output.push('');
      output.push(...item.content);
      output.push('');
      output.push(`${indent}</TabItem>`);
    }
    output.push(`${indent}</Tabs>`);
  }

  return { source: output.join('\n'), converted };
}

function toPosix(value) {
  return value.split(path.sep).join('/');
}

function isPathWithin(root, target) {
  const relative = path.relative(root, target);
  return (
    relative === '' ||
    (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative))
  );
}

async function resolveCodeSource({ requestedSource, sourcePath, sourceLine, allowedRoots }) {
  const roots = allowedRoots.map((root) => path.resolve(root));
  const boundary = roots.join(', ');
  const context = `${sourcePath}:${sourceLine}`;
  if (path.isAbsolute(requestedSource)) {
    throw new Error(
      `Code source '${requestedSource}' in ${context} must be relative and remain within allowed snippet root(s): ${boundary}.`,
    );
  }

  const logicalPath = path.resolve(path.dirname(sourcePath), requestedSource);
  const containingRootIndexes = roots
    .map((root, index) => (isPathWithin(root, logicalPath) ? index : -1))
    .filter((index) => index >= 0);
  if (containingRootIndexes.length === 0) {
    throw new Error(
      `Code source '${requestedSource}' in ${context} resolves outside allowed snippet root(s): ${boundary}.`,
    );
  }
  try {
    await access(logicalPath);
  } catch {
    throw new Error(
      `Code source '${requestedSource}' in ${context} does not exist (${logicalPath}). Allowed snippet root(s): ${boundary}.`,
    );
  }

  const [physicalPath, ...physicalRoots] = await Promise.all([
    realpath(logicalPath),
    ...roots.map((root) => realpath(root)),
  ]);
  if (
    !containingRootIndexes.some((rootIndex) =>
      isPathWithin(physicalRoots[rootIndex], physicalPath),
    )
  ) {
    throw new Error(
      `Code source '${requestedSource}' in ${context} resolves through a link outside allowed snippet root(s): ${boundary}.`,
    );
  }

  return logicalPath;
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function escapeMarkdown(value) {
  return value.replaceAll('\\', '\\\\').replaceAll('[', '\\[').replaceAll(']', '\\]');
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

async function pathExists(filePath) {
  try {
    await access(filePath);
    if (process.platform === 'win32') {
      return (await realpath(filePath)) === path.resolve(filePath);
    }
    return true;
  } catch {
    return false;
  }
}

function isWithinRoot(rootPath, targetPath) {
  const relative = path.relative(rootPath, targetPath);
  return (
    relative.length === 0 ||
    (!relative.startsWith(`..${path.sep}`) &&
      relative !== '..' &&
      !path.isAbsolute(relative))
  );
}

async function resolveIncludePath(sourcePath, reference, includeRoot) {
  if (
    reference.length === 0 ||
    reference.includes('\0') ||
    reference.includes('\\') ||
    reference.split('/').some((segment) => segment.includes(':')) ||
    path.isAbsolute(reference) ||
    path.posix.isAbsolute(reference) ||
    path.win32.isAbsolute(reference) ||
    /^[a-z][a-z\d+.-]*:/i.test(reference)
  ) {
    throw new Error(`Unsafe INCLUDE path '${reference}' in ${sourcePath}.`);
  }

  const resolvedRoot = await realpath(includeRoot);
  const candidate = path.resolve(path.dirname(sourcePath), reference);
  if (!isWithinRoot(resolvedRoot, candidate)) {
    throw new Error(`INCLUDE '${reference}' in ${sourcePath} resolves outside ${resolvedRoot}.`);
  }

  let target;
  try {
    target = await realpath(candidate);
  } catch {
    throw new Error(`INCLUDE '${reference}' in ${sourcePath} does not exist (${candidate}).`);
  }
  if (!isWithinRoot(resolvedRoot, target)) {
    throw new Error(`INCLUDE '${reference}' in ${sourcePath} resolves outside ${resolvedRoot}.`);
  }
  return target;
}

function rebaseReference(reference, fromFile, toFile) {
  if (
    reference.length === 0 ||
    reference.startsWith('#') ||
    reference.startsWith('/') ||
    reference.startsWith('~') ||
    /^[a-z][a-z\d+.-]*:/i.test(reference)
  ) {
    return reference;
  }

  const match = /^([^?#]+)([?#].*)?$/.exec(reference);
  if (!match) {
    return reference;
  }
  const [, pathname, suffix = ''] = match;
  const absolutePath = path.resolve(path.dirname(fromFile), pathname);
  let rebased = toPosix(path.relative(path.dirname(toFile), absolutePath));
  if (!rebased.startsWith('.')) {
    rebased = `./${rebased}`;
  }
  return `${rebased}${suffix}`;
}

function rebaseIncludedReferences(source, includePath, consumingPath) {
  return transformOutsideCodeBlocks(source, (segment) => {
    let rebased = segment.replace(
      /(\b(?:source|lightbox)=")([^"]+)(")/g,
      (_match, prefix, reference, suffix) =>
        `${prefix}${rebaseReference(reference, includePath, consumingPath)}${suffix}`,
    );
    rebased = rebased.replace(
      /(\]\()([^) \t]+)(\))/g,
      (_match, prefix, reference, suffix) =>
        `${prefix}${rebaseReference(reference, includePath, consumingPath)}${suffix}`,
    );
    rebased = rebased.replace(
      /(\b(?:href|src)=")([^"]+)(")/g,
      (_match, prefix, reference, suffix) =>
        `${prefix}${rebaseReference(reference, includePath, consumingPath)}${suffix}`,
    );
    return rebased;
  });
}

export function splitFrontmatter(source) {
  const normalized = source.replace(/^\uFEFF/, '');
  const match = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(normalized);
  if (!match) {
    return { metadata: {}, body: normalized, bodyStartLine: 1 };
  }

  const metadata = YAML.parse(match[1]) ?? {};
  if (typeof metadata !== 'object' || Array.isArray(metadata)) {
    throw new Error('DocFX frontmatter must be a YAML object.');
  }

  return {
    metadata,
    body: normalized.slice(match[0].length),
    bodyStartLine: match[0].split(/\r?\n/).length,
  };
}

function inferTitle(filePath) {
  const basename = path.basename(filePath, path.extname(filePath));
  const candidate = basename.toLowerCase() === 'index' ? path.basename(path.dirname(filePath)) : basename;
  return candidate
    .replaceAll('-', ' ')
    .replaceAll('_', ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function parseMsDate(value, filePath) {
  if (value instanceof Date && !Number.isNaN(value.valueOf())) {
    return value;
  }
  if (typeof value !== 'string') {
    return undefined;
  }

  const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value.trim());
  if (!match) {
    throw new Error(`Invalid ms.date '${value}' in ${filePath}.`);
  }

  const [, month, day, year] = match;
  const date = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)));
  if (
    date.getUTCFullYear() !== Number(year) ||
    date.getUTCMonth() !== Number(month) - 1 ||
    date.getUTCDate() !== Number(day)
  ) {
    throw new Error(`Invalid ms.date '${value}' in ${filePath}.`);
  }

  return date;
}

function serializeFrontmatter(metadata, filePath, overrides = {}) {
  const title =
    typeof overrides.title === 'string'
      ? overrides.title
      : typeof metadata.title === 'string'
        ? metadata.title
        : inferTitle(filePath);
  const frontmatter = {
    title,
  };

  const description = overrides.description ?? metadata.description;
  if (typeof description === 'string' && description.length > 0) {
    frontmatter.description = description;
  }

  const lastUpdated = parseMsDate(metadata['ms.date'], filePath);
  if (lastUpdated) {
    frontmatter.lastUpdated = lastUpdated;
  }

  Object.assign(frontmatter, overrides.frontmatter);
  if (Object.keys(metadata).length > 0) {
    frontmatter.docfx = metadata;
  }

  return `---\n${YAML.stringify(frontmatter, { lineWidth: 0 }).trimEnd()}\n---\n\n`;
}

export function parseDirectiveAttributes(value, context) {
  const attributes = {};
  const consumed = [];
  for (const match of value.matchAll(directiveAttributePattern)) {
    const [text, key, attributeValue] = match;
    if (Object.hasOwn(attributes, key)) {
      throw new Error(`Duplicate '${key}' attribute in ${context}.`);
    }
    attributes[key] = attributeValue;
    consumed.push({ index: match.index, length: text.length });
  }

  let remainder = value;
  for (const item of consumed.reverse()) {
    remainder = remainder.slice(0, item.index) + remainder.slice(item.index + item.length);
  }
  if (remainder.trim().length > 0) {
    throw new Error(`Unsupported directive syntax '${remainder.trim()}' in ${context}.`);
  }

  return attributes;
}

async function expandIncludesInternal(source, sourcePath, stack, includeRoot, codeOptions) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const output = [];
  const protectedLineRanges = /\[!INCLUDE|:::code/.test(source)
    ? markdownDirectiveProtectedLineRanges(source)
    : [];

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (lineOverlapsRanges(index + 1, protectedLineRanges)) {
      output.push(line);
      continue;
    }
    const match = /^(\s*)\[!INCLUDE\s+\[([^\]]+)\]\(([^)]+)\)\]\s*$/.exec(line);
    if (!match) {
      if (line.includes('[!INCLUDE')) {
        throw new Error(`Unsupported INCLUDE syntax in ${sourcePath}: ${line.trim()}`);
      }
      output.push(
        codeOptions
          ? await convertCodeDirectives(
              line,
              sourcePath,
              codeOptions.allowedRoots,
              codeOptions.firstSourceLine + index,
            )
          : line,
      );
      continue;
    }

    const [, indent, label, relativePath] = match;
    const includePath = await resolveIncludePath(sourcePath, relativePath, includeRoot);
    if (stack.includes(includePath)) {
      throw new Error(`Circular INCLUDE detected: ${[...stack, includePath].join(' -> ')}`);
    }

    const includeSource = await readFile(includePath, 'utf8');
    const { body, bodyStartLine } = splitFrontmatter(includeSource);
    const expanded = rebaseIncludedReferences(
      await expandIncludesInternal(
        body,
        includePath,
        [...stack, includePath],
        includeRoot,
        codeOptions ? { ...codeOptions, firstSourceLine: bodyStartLine } : undefined,
      ),
      includePath,
      sourcePath,
    );
    const relativeDisplay = toPosix(path.relative(path.dirname(sourcePath), includePath));
    output.push(`${indent}<!-- INCLUDE ${escapeHtml(label)}: ${escapeHtml(relativeDisplay)} -->`);
    output.push(
      ...expanded
        .replace(/\n$/, '')
        .split('\n')
        .map((includedLine) => `${indent}${includedLine}`),
    );
    output.push(`${indent}<!-- END INCLUDE ${escapeHtml(label)} -->`);
  }

  return output.join('\n');
}

export async function expandIncludes(source, sourcePath, includeRoot = path.dirname(sourcePath)) {
  const resolvedRoot = await realpath(includeRoot);
  let resolvedSource;
  try {
    resolvedSource = await realpath(sourcePath);
  } catch {
    resolvedSource = path.resolve(sourcePath);
  }
  if (!isWithinRoot(resolvedRoot, resolvedSource)) {
    throw new Error(`Documentation source '${resolvedSource}' is outside ${resolvedRoot}.`);
  }
  return expandIncludesInternal(source, resolvedSource, [resolvedSource], includeRoot);
}

export async function collectIncludeTargets(markdownFiles, optionsOrRoot = {}) {
  const options =
    typeof optionsOrRoot === 'string' ? { allowedRoot: optionsOrRoot } : optionsOrRoot;
  return collectIncludeTargetsWithoutYaml(markdownFiles, {
    ...options,
    splitFrontmatter,
  });
}

function findRegion(lines, id, sourcePath) {
  const escapedId = escapeRegExp(id);
  const styles = [
    {
      open: new RegExp(`^\\s*//\\s*<${escapedId}>\\s*$`),
      close: new RegExp(`^\\s*//\\s*</${escapedId}>\\s*$`),
      nestedOpen: /^\s*\/\/\s*<[^/][^>]*>\s*$/,
      nestedClose: /^\s*\/\/\s*<\/[^>]+>\s*$/,
    },
    {
      open: new RegExp(`^\\s*<!--\\s*<${escapedId}>\\s*-->\\s*$`),
      close: new RegExp(`^\\s*<!--\\s*</${escapedId}>\\s*-->\\s*$`),
      nestedOpen: /^\s*<!--\s*<[^/][^>]*>\s*-->\s*$/,
      nestedClose: /^\s*<!--\s*<\/[^>]+>\s*-->\s*$/,
    },
    {
      open: new RegExp(`^\\s*#region\\s+${escapedId}\\s*$`, 'i'),
      close: /^\s*#endregion\b/i,
      nestedOpen: /^\s*#region\b/i,
      nestedClose: /^\s*#endregion\b/i,
    },
  ];

  for (const style of styles) {
    const openings = lines
      .map((line, index) => (style.open.test(line) ? index : -1))
      .filter((index) => index >= 0);
    if (openings.length > 1) {
      throw new Error(`Snippet region '${id}' occurs more than once in ${sourcePath}.`);
    }
    if (openings.length === 0) {
      continue;
    }

    const start = openings[0];
    let depth = 0;
    for (let index = start + 1; index < lines.length; index += 1) {
      if (style.nestedOpen.test(lines[index])) {
        depth += 1;
      }
      if (style.nestedClose.test(lines[index])) {
        if (depth === 0 && style.close.test(lines[index])) {
          return lines.slice(start + 1, index);
        }
        depth -= 1;
      }
    }
    throw new Error(`Snippet region '${id}' in ${sourcePath} is not closed.`);
  }

  throw new Error(`Snippet region '${id}' was not found in ${sourcePath}.`);
}

function selectRanges(lines, range, sourcePath) {
  const selected = [];
  for (const part of range.split(',').map((value) => value.trim())) {
    let start;
    let end;
    let match;
    if ((match = /^(\d+)$/.exec(part))) {
      start = Number(match[1]);
      end = start;
    } else if ((match = /^(\d+)-(\d+)$/.exec(part))) {
      start = Number(match[1]);
      end = Number(match[2]);
    } else if ((match = /^(\d+)-$/.exec(part))) {
      start = Number(match[1]);
      end = lines.length;
    } else if ((match = /^-(\d+)$/.exec(part))) {
      start = 1;
      end = Number(match[1]);
    } else {
      throw new Error(`Unsupported range '${part}' for ${sourcePath}.`);
    }

    if (start < 1 || end < start || end > lines.length) {
      throw new Error(
        `Range '${part}' is outside ${sourcePath}, which contains ${lines.length} lines.`,
      );
    }
    selected.push(...lines.slice(start - 1, end));
  }
  return selected;
}

function dedent(lines) {
  const nonEmpty = lines.filter((line) => line.trim().length > 0);
  if (nonEmpty.length === 0) {
    return lines;
  }
  const indentation = Math.min(
    ...nonEmpty.map((line) => /^(\s*)/.exec(line)?.[1].replaceAll('\t', '    ').length ?? 0),
  );
  return lines.map((line) => {
    let remaining = indentation;
    let index = 0;
    while (remaining > 0 && index < line.length) {
      remaining -= line[index] === '\t' ? 4 : 1;
      index += 1;
    }
    return line.slice(index);
  });
}

function languageFor(sourcePath, requestedLanguage) {
  if (requestedLanguage) {
    return requestedLanguage.toLowerCase() === 'c#' ? 'csharp' : requestedLanguage;
  }
  const extension = path.extname(sourcePath).toLowerCase();
  return (
    {
      '.cs': 'csharp',
      '.csproj': 'xml',
      '.config': 'xml',
      '.json': 'json',
      '.props': 'xml',
      '.ps1': 'powershell',
      '.sh': 'bash',
      '.xml': 'xml',
      '.yml': 'yaml',
      '.yaml': 'yaml',
    }[extension] ?? 'text'
  );
}

function codeFenceFor(lines) {
  const maximum = Math.max(
    0,
    ...lines.flatMap((line) => [...line.matchAll(/`+/g)].map((match) => match[0].length)),
  );
  return '`'.repeat(Math.max(3, maximum + 1));
}

async function convertCodeDirectives(source, sourcePath, allowedRoots, firstSourceLine) {
  const output = [];
  const sourceLines = source.replaceAll('\r\n', '\n').split('\n');
  for (let index = 0; index < sourceLines.length; index += 1) {
    const line = sourceLines[index];
    const sourceLine = firstSourceLine + index;
    const match = /^(\s*):::code\s+(.+?)\s*$/.exec(line);
    if (!match) {
      if (line.includes(':::code')) {
        throw new Error(`Unsupported code directive in ${sourcePath}:${sourceLine}: ${line.trim()}`);
      }
      output.push(line);
      continue;
    }

    const [, indent, rawAttributeSource] = match;
    const attributeSource = rawAttributeSource.endsWith(':::')
      ? rawAttributeSource.slice(0, -3).trimEnd()
      : rawAttributeSource;
    const attributes = parseDirectiveAttributes(
      attributeSource,
      `code directive in ${sourcePath}:${sourceLine}`,
    );
    const unknown = Object.keys(attributes).filter(
      (key) => !['highlight', 'id', 'language', 'range', 'source'].includes(key),
    );
    if (unknown.length > 0) {
      throw new Error(
        `Unsupported code attributes '${unknown.join(', ')}' in ${sourcePath}:${sourceLine}.`,
      );
    }
    if (!attributes.source) {
      throw new Error(
        `A code directive in ${sourcePath}:${sourceLine} is missing its source attribute.`,
      );
    }

    const snippetPath = await resolveCodeSource({
      requestedSource: attributes.source,
      sourcePath,
      sourceLine,
      allowedRoots,
    });

    let lines = (await readFile(snippetPath, 'utf8'))
      .replace(/^\uFEFF/, '')
      .replaceAll('\r\n', '\n')
      .split('\n');
    if (lines.at(-1) === '') {
      lines.pop();
    }
    if (attributes.id) {
      lines = findRegion(lines, attributes.id, snippetPath);
    }
    if (attributes.range) {
      lines = selectRanges(lines, attributes.range, snippetPath);
    }
    lines = dedent(lines);

    const relativeSource = toPosix(path.relative(path.dirname(sourcePath), snippetPath));
    const provenance = [
      `Source: ${relativeSource}`,
      attributes.id ? `region: ${attributes.id}` : undefined,
      attributes.range ? `range: ${attributes.range}` : undefined,
      attributes.highlight ? `highlight: ${attributes.highlight}` : undefined,
    ]
      .filter(Boolean)
      .join('; ');
    const fence = codeFenceFor(lines);
    output.push(`${indent}<!-- ${escapeHtml(provenance)} -->`);
    output.push(`${indent}${fence}${languageFor(snippetPath, attributes.language)}`);
    output.push(...lines.map((snippetLine) => `${indent}${snippetLine}`));
    output.push(`${indent}${fence}`);
  }
  return output.join('\n');
}

function convertCallouts(source, sourcePath) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const output = [];
  const blockquoteLineRanges = />\s*\[!(?:NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]/.test(source)
    ? markdownBlockquoteLineRanges(source)
    : [];
  const literalLineRanges = blockquoteLineRanges.length > 0
    ? markdownLiteralLineRanges(source)
    : [];
  const kinds = {
    CAUTION: ['danger', 'Caution'],
    IMPORTANT: ['note', 'Important'],
    NOTE: ['note', 'Note'],
    TIP: ['tip', 'Tip'],
    WARNING: ['caution', 'Warning'],
  };

  for (let index = 0; index < lines.length; index += 1) {
    if (lineOverlapsRanges(index + 1, literalLineRanges)) {
      output.push(lines[index]);
      continue;
    }
    const match = /^(\s*)>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/.exec(lines[index]);
    if (!match) {
      if (
        />\s*\[![A-Z]+\]/.test(lines[index]) &&
        !/>\s*\[!INCLUDE\b/.test(lines[index])
      ) {
        throw new Error(`Unsupported callout in ${sourcePath}: ${lines[index].trim()}`);
      }
      output.push(lines[index]);
      continue;
    }

    const [, indent, kind] = match;
    const [variant, title] = kinds[kind];
    const body = collectBlockquoteBody(
      lines,
      index,
      indent,
      blockquoteLineRanges,
    );
    index = body.endIndex;
    output.push(`${indent}:::${variant}[${title}]`);
    output.push(
      ...escapeExposedLiteralDirectives(body.lines).map(
        (bodyLine) => `${indent}${bodyLine}`,
      ),
    );
    output.push(`${indent}:::`);
  }

  return output.join('\n');
}

function convertLearnBlocks(source, sourcePath) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const output = [];
  const blockquoteLineRanges = source.includes('[!div')
    ? markdownBlockquoteLineRanges(source)
    : [];
  const literalLineRanges = /\[!(?:div|VIDEO)\b/.test(source)
    ? markdownLiteralLineRanges(source)
    : [];

  for (let index = 0; index < lines.length; index += 1) {
    if (lineOverlapsRanges(index + 1, literalLineRanges)) {
      output.push(lines[index]);
      continue;
    }
    const video = /^(\s*)>\s*\[!VIDEO\s+(\S+)\]\s*$/.exec(lines[index]);
    if (video) {
      const [, indent, videoUrl] = video;
      let parsedUrl;
      try {
        parsedUrl = new URL(videoUrl);
      } catch {
        throw new Error(`Invalid VIDEO URL '${videoUrl}' in ${sourcePath}.`);
      }
      if (parsedUrl.protocol !== 'https:') {
        throw new Error(`VIDEO URL '${videoUrl}' in ${sourcePath} must use HTTPS.`);
      }
      output.push(
        `${indent}<div class="video-embed">`,
        `${indent}  <iframe src="${escapeHtml(videoUrl)}" title="Orleans video" loading="lazy" allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture" allowFullScreen />`,
        `${indent}</div>`,
      );
      continue;
    }

    const container = /^(\s*)>\s*\[!div\s+class=["“]([^"”]+)["”]\]\s*$/.exec(lines[index]);
    if (!container) {
      output.push(lines[index]);
      continue;
    }

    const [, indent, className] = container;
    const body = collectBlockquoteBody(
      lines,
      index,
      indent,
      blockquoteLineRanges,
    );
    index = body.endIndex;
    const escapedBody = escapeExposedLiteralDirectives(body.lines);

    if (className === 'nextstepaction') {
      output.push(
        `${indent}:::tip[Next step]`,
        ...escapedBody.map((line) => `${indent}${line}`),
        `${indent}:::`,
      );
    } else if (className === 'checklist') {
      output.push(...escapedBody.map((line) => `${indent}${line}`));
    } else {
      throw new Error(`Unsupported Learn div class '${className}' in ${sourcePath}.`);
    }
  }

  return output.join('\n');
}

function collectBlockquoteBody(lines, startIndex, indent, protectedLineRanges) {
  const startLine = startIndex + 1;
  const blockquoteEnd =
    Math.max(
      startLine,
      ...protectedLineRanges
        .filter(([rangeStart, rangeEnd]) => rangeStart <= startLine && startLine <= rangeEnd)
        .map(([, rangeEnd]) => rangeEnd),
    ) - 1;
  const body = [];
  let endIndex = startIndex;
  while (endIndex + 1 < lines.length && endIndex + 1 <= blockquoteEnd) {
    const nextLine = lines[endIndex + 1];
    const contentMatch = new RegExp(`^${escapeRegExp(indent)}> ?(.*)$`).exec(nextLine);
    body.push(
      contentMatch
        ? contentMatch[1]
        : nextLine.slice(
            Math.min(indent.length, /^ */.exec(nextLine)[0].length),
          ),
    );
    endIndex += 1;
  }
  return { lines: body, endIndex };
}

function escapeExposedLiteralDirectives(lines) {
  const protectedLineRanges = markdownDirectiveProtectedLineRanges(lines.join('\n'));
  return lines.map((line, index) => {
    if (lineOverlapsRanges(index + 1, protectedLineRanges)) {
      return line;
    }
    return line
      .replaceAll('[!INCLUDE', '&#91;!INCLUDE')
      .replaceAll(':::code', '&#58;&#58;&#58;code');
  });
}

async function convertImages(source, sourcePath) {
  const output = [];
  for (const line of source.split('\n')) {
    const match = /^(\s*):::image\s+(.+?):::\s*$/.exec(line);
    if (!match) {
      if (line.includes(':::image')) {
        throw new Error(`Unsupported image directive in ${sourcePath}: ${line.trim()}`);
      }
      output.push(line);
      continue;
    }

    const [, indent, attributeSource] = match;
    const attributes = parseDirectiveAttributes(attributeSource, `image directive in ${sourcePath}`);
    const unknown = Object.keys(attributes).filter(
      (key) => !['alt-text', 'lightbox', 'source', 'type'].includes(key),
    );
    if (unknown.length > 0) {
      throw new Error(`Unsupported image attributes '${unknown.join(', ')}' in ${sourcePath}.`);
    }
    if (!attributes.source || !attributes['alt-text']) {
      throw new Error(`An image directive in ${sourcePath} requires source and alt-text.`);
    }
    for (const [name, target] of [
      ['source', attributes.source],
      ['lightbox', attributes.lightbox],
    ]) {
      if (
        target &&
        !target.startsWith('/') &&
        !target.startsWith('~') &&
        !/^[a-z][a-z\d+.-]*:/i.test(target)
      ) {
        const pathname = /^[^?#]+/.exec(target)?.[0] ?? target;
        const imagePath = path.resolve(path.dirname(sourcePath), pathname);
        if (!(await pathExists(imagePath))) {
          throw new Error(
            `Image ${name} '${target}' in ${sourcePath} does not exist with that exact path (${imagePath}).`,
          );
        }
      }
    }

    const alt = escapeMarkdown(attributes['alt-text']);
    if (attributes.lightbox) {
      output.push(
        `${indent}<div class="image-lightbox" data-image-lightbox>`,
        '',
        `${indent}![${alt}](${attributes.source})`,
        '',
        `${indent}</div>`,
      );
    } else {
      output.push(`${indent}![${alt}](${attributes.source})`);
    }
  }
  return output.join('\n');
}

function formatPivot(pivot) {
  const match = /^orleans-(\d+)-(x|\d+)$/.exec(pivot);
  if (match) {
    return `Orleans ${match[1]}.${match[2]}`;
  }
  const labels = {
    'azure-cosmos-db-nosql': 'Azure Cosmos DB for NoSQL',
    'azure-storage': 'Azure Storage',
  };
  if (labels[pivot]) {
    return labels[pivot];
  }
  throw new Error(`Unsupported documentation pivot '${pivot}'.`);
}

function convertZones(source, sourcePath) {
  const output = [];
  const stack = [];
  const protectedLineRanges = markdownDirectiveProtectedLineRanges(source);
  const lines = source.split('\n');
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (lineOverlapsRanges(index + 1, protectedLineRanges)) {
      output.push(line);
      continue;
    }

    const start = /^(\s*):::\s*zone\s+(.+?)\s*$/.exec(line);
    if (start) {
      if (stack.length > 0) {
        throw new Error(`Nested version zones are not supported in ${sourcePath}.`);
      }
      const [, indent, attributeSource] = start;
      const attributes = parseDirectiveAttributes(attributeSource, `version zone in ${sourcePath}`);
      const unknown = Object.keys(attributes).filter((key) => !['pivot', 'target'].includes(key));
      if (
        unknown.length > 0 ||
        (attributes.target !== undefined && attributes.target !== 'docs') ||
        !attributes.pivot
      ) {
        throw new Error(`Unsupported version zone in ${sourcePath}: ${line.trim()}`);
      }
      const versions = attributes.pivot
        .split(',')
        .map((pivot) => formatPivot(pivot.trim()))
        .join(', ');
      stack.push(indent);
      output.push(`${indent}::::version{versions="${versions}"}`);
      continue;
    }

    const end = /^(\s*):::\s*zone-end\s*$/.exec(line);
    if (end) {
      if (stack.length === 0) {
        throw new Error(`Unmatched zone-end in ${sourcePath}.`);
      }
      const indent = stack.pop();
      output.push(`${indent}::::`);
      continue;
    }

    if (/:::\s*zone(?:-end)?\b/.test(line)) {
      throw new Error(`Unsupported version zone in ${sourcePath}: ${line.trim()}`);
    }
    output.push(line);
  }
  if (stack.length > 0) {
    throw new Error(`Unclosed version zone in ${sourcePath}.`);
  }
  return output.join('\n');
}

function humanizeXref(uid, displayProperty) {
  const withoutParameters = uid.replace(/\(.*$/, '').replace(/\*$/, '');
  let display = withoutParameters;
  if (displayProperty !== 'fullName') {
    const parts = withoutParameters.split('.');
    display =
      displayProperty === 'nameWithType' && parts.length > 1
        ? parts.slice(-2).join('.')
        : parts.at(-1);
  }
  return display.replace(/`(\d+)/g, (_, count) => {
    const arity = Number(count);
    const conventionalNames = ['T', 'U', 'V', 'W', 'X', 'Y', 'Z'];
    const names = Array.from(
      { length: arity },
      (_value, index) => conventionalNames[index] ?? `T${index + 1}`,
    );
    return `&lt;${names.join(', ')}&gt;`;
  });
}

function xrefUrl(uid, uidMap) {
  if (uidMap.has(uid)) {
    return uidMap.get(uid);
  }
  const withoutSignature = uid.replace(/\*$/, '').replace(/\(.*$/, '');
  const slug = withoutSignature.replace(/`(\d+)/g, '-$1').toLowerCase();
  return `https://learn.microsoft.com/dotnet/api/${encodeURI(slug)}`;
}

function findMarkdownDestinationEnd(source, start) {
  let depth = 1;
  for (let index = start + 1; index < source.length; index += 1) {
    if (source[index] === '\\') {
      index += 1;
    } else if (source[index] === '(') {
      depth += 1;
    } else if (source[index] === ')' && --depth === 0) {
      return index;
    }
  }
  return -1;
}

function collectMarkdownLinks(source) {
  const links = [];
  const labelStarts = [];
  let codeDelimiterLength = 0;
  for (let index = 0; index < source.length; index += 1) {
    if (codeDelimiterLength === 0 && source[index] === '\\') {
      index += 1;
      continue;
    }
    if (codeDelimiterLength === 0 && source.startsWith('<xref:', index)) {
      const end = source.indexOf('>', index + '<xref:'.length);
      if (end < 0) {
        break;
      }
      index = end;
      continue;
    }
    if (source[index] === '`') {
      let end = index + 1;
      while (source[end] === '`') {
        end += 1;
      }
      const delimiterLength = end - index;
      if (codeDelimiterLength === 0) {
        codeDelimiterLength = delimiterLength;
      } else if (delimiterLength === codeDelimiterLength) {
        codeDelimiterLength = 0;
      }
      index = end - 1;
      continue;
    }
    if (codeDelimiterLength !== 0) {
      continue;
    }
    if (source[index] === '[') {
      labelStarts.push(index);
      continue;
    }
    if (source[index] !== ']' || labelStarts.length === 0 || source[index + 1] !== '(') {
      if (source[index] === ']') {
        labelStarts.pop();
      }
      continue;
    }

    const labelStart = labelStarts.pop();
    const labelEnd = index;
    const destinationEnd = findMarkdownDestinationEnd(source, labelEnd + 1);
    if (destinationEnd < 0) {
      break;
    }
    links.push({
      start: labelStart,
      labelEnd,
      end: destinationEnd,
      label: source.slice(labelStart + 1, labelEnd),
      destination: source.slice(labelEnd + 2, destinationEnd),
    });
    labelStarts.length = 0;
    index = destinationEnd;
  }
  return links;
}

function replaceMarkdownLinks(source, transform) {
  const links = collectMarkdownLinks(source);
  let output = '';
  let consumed = 0;
  for (const link of links) {
    const replacement = transform(link);
    if (replacement === undefined) {
      continue;
    }
    output += source.slice(consumed, link.start) + replacement;
    consumed = link.end + 1;
  }
  return output + source.slice(consumed);
}

function convertXrefs(line, uidMap) {
  let converted = replaceMarkdownLinks(line, (link) => {
    let destination = link.destination;
    if (destination.startsWith('<') && destination.endsWith('>')) {
      destination = destination.slice(1, -1);
    }
    if (destination.startsWith('xref:')) {
      const [uid] = destination.slice('xref:'.length).split('?');
      const label = link.label.replace(/\\</g, '&lt;').replace(/\\>/g, '&gt;');
      return `[${label}](${xrefUrl(uid, uidMap)})`;
    }
    if (link.label.includes('<xref:')) {
      const plainLabel = link.label.replace(/<xref:([^>]+)>/g, (_xref, reference) => {
        const [uid, query = ''] = reference.split('?');
        return escapeMarkdown(
          humanizeXref(uid, new URLSearchParams(query).get('displayProperty')),
        );
      });
      return `[${plainLabel}](${link.destination})`;
    }
    return undefined;
  });
  converted = converted.replace(/<xref:([^>]+)>/g, (_, reference) => {
    const [uid, query = ''] = reference.split('?');
    const displayProperty = new URLSearchParams(query).get('displayProperty');
    return `[${escapeMarkdown(humanizeXref(uid, displayProperty))}](${xrefUrl(uid, uidMap)})`;
  });
  converted = converted.replace(
    /(?<![\w/:])xref:([A-Za-z0-9_./+`#*-]+)/g,
    (_, uid) => `[${escapeMarkdown(humanizeXref(uid))}](${xrefUrl(uid, uidMap)})`,
  );
  return converted;
}

function routeTarget(target) {
  const match = /^([^?#]+)([?#].*)?$/.exec(target);
  if (!match) {
    return target;
  }
  let [, pathname, suffix = ''] = match;
  if (!markdownExtensions.has(path.extname(pathname).toLowerCase())) {
    return target;
  }

  pathname = pathname.slice(0, -path.extname(pathname).length);
  if (path.posix.basename(pathname).toLowerCase() === 'index') {
    pathname = path.posix.dirname(pathname);
  }
  if (pathname === '.') {
    pathname = './';
  } else if (!pathname.endsWith('/')) {
    pathname += '/';
  }
  return `${pathname}${suffix}`;
}

function convertLinkTarget(target, sourcePath, sourceRoot) {
  const bracketedUrl = /^<(https?:\/\/[^>]+)>$/.exec(target);
  if (bracketedUrl) {
    return bracketedUrl[1];
  }
  if (
    target.startsWith('#') ||
    target.startsWith('~') ||
    /^[a-z][a-z\d+.-]*:/i.test(target)
  ) {
    return target;
  }

  if (target.startsWith('/')) {
    if (target === siteBase || target.startsWith(`${siteBase}/`)) {
      return target;
    }
    if (target === learnDocsPrefix || target.startsWith(`${learnDocsPrefix}/`)) {
      const local = target.slice(learnDocsPrefix.length);
      return `${siteBase}${local || '/'}`;
    }
    return `https://learn.microsoft.com${target}`;
  }

  const pathname = /^[^?#]+/.exec(target)?.[0] ?? target;
  const extension = path.posix.extname(pathname).toLowerCase();
  if (extension && !markdownExtensions.has(extension)) {
    return target;
  }

  const sourceRelative = toPosix(path.relative(sourceRoot, sourcePath));
  const sourceDirectory = path.posix.dirname(sourceRelative);
  const learnBase = new URL(
    `${learnDocsPrefix}/${sourceDirectory === '.' ? '' : `${sourceDirectory}/`}`,
    'https://learn.microsoft.com',
  );
  const resolved = new URL(routeTarget(target), learnBase);
  if (
    resolved.pathname === learnDocsPrefix ||
    resolved.pathname.startsWith(`${learnDocsPrefix}/`)
  ) {
    const local = resolved.pathname.slice(learnDocsPrefix.length);
    return `${siteBase}${local || '/'}${resolved.search}${resolved.hash}`;
  }
  return resolved.href;
}

function convertLinks(line, sourcePath, sourceRoot) {
  return replaceMarkdownLinks(line, (link) => {
    if (link.start > 0 && line[link.start - 1] === '!') {
      return undefined;
    }

    let normalizedDestination = link.destination;
    if (normalizedDestination.startsWith('<') && normalizedDestination.endsWith('>')) {
      normalizedDestination = normalizedDestination.slice(1, -1);
    }
    if (normalizedDestination.startsWith('xref:')) {
      return undefined;
    }

    let targetEnd = link.destination.length;
    if (link.destination.startsWith('<')) {
      const closingBracket = link.destination.indexOf('>');
      if (closingBracket < 0) {
        return undefined;
      }
      targetEnd = closingBracket + 1;
    } else {
      const whitespace = /\s/.exec(link.destination);
      if (whitespace) {
        targetEnd = whitespace.index;
      }
    }

    const target = link.destination.slice(0, targetEnd);
    const remainder = link.destination.slice(targetEnd).trim();
    const title = remainder.length > 0 ? /^"([^"]*)"$/.exec(remainder)?.[1] : undefined;
    if (target.length === 0 || (remainder.length > 0 && title === undefined)) {
      return undefined;
    }

    const convertedTarget = convertLinkTarget(target, sourcePath, sourceRoot);
    return `[${link.label}](${convertedTarget}${title ? ` "${title}"` : ''})`;
  });
}

function openCodeFence(line) {
  const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
  return marker ? { character: marker[0], length: marker.length } : undefined;
}

function closesCodeFence(line, fence) {
  return new RegExp(`^\\s*${fence.character}{${fence.length},}\\s*$`).test(line);
}

function transformOutsideCodeFences(source, transform) {
  let fence;
  return source
    .split('\n')
    .map((line) => {
      if (!fence) {
        fence = openCodeFence(line);
        if (fence) {
          return line;
        }
      } else if (closesCodeFence(line, fence)) {
        fence = undefined;
        return line;
      }
      return fence ? line : transform(line);
    })
    .join('\n');
}

function transformOutsideCodeBlocks(source, transform) {
  const chunks = [];
  let buffer = [];
  let fence;

  function flush(insideCode) {
    if (buffer.length === 0) {
      return;
    }
    const value = buffer.join('\n');
    chunks.push(insideCode ? value : transform(value));
    buffer = [];
  }

  for (const line of source.split('\n')) {
    if (!fence) {
      const opening = openCodeFence(line);
      if (opening) {
        flush(false);
        fence = opening;
      }
    }
    buffer.push(line);
    if (fence && closesCodeFence(line, fence) && buffer.length > 1) {
      flush(true);
      fence = undefined;
    }
  }
  flush(Boolean(fence));
  return chunks.join('\n');
}

function convertHtmlCommentsForMdx(source) {
  return transformOutsideCodeBlocks(source, (segment) =>
    segment.replace(/<!--([\s\S]*?)-->/g, (_match, content) => `{/*${content}*/}`),
  );
}

function escapeMdxAngles(source) {
  const htmlTags =
    'a|abbr|b|blockquote|br|code|dd|details|div|dl|dt|em|hr|i|iframe|img|input|kbd|li|ol|p|pre|source|span|strong|sub|summary|sup|tabitem|table|tabs|tbody|td|th|thead|tr|ul';
  const pattern = new RegExp(
    `<(?!\\/?(?:${htmlTags})\\b|https?:\\/\\/|mailto:)(?=[A-Za-z\\d=/])`,
    'gi',
  );
  return source
    .replace(/<(https?:\/\/[^>]+)>/g, '[$1]($1)')
    .replace(/<mailto:([^>]+)>/g, '[$1](mailto:$1)')
    .replace(pattern, '&lt;')
    .replace(/<(br|hr)\s*>/gi, '<$1 />')
    .replace(/<(img|input|source)(\b[^>]*?)(?<!\/)>/gi, '<$1$2 />');
}

function stripHtmlTags(value) {
  let result = '';
  let insideTag = false;
  for (const character of value) {
    if (character === '<') {
      insideTag = true;
    } else if (character === '>') {
      insideTag = false;
    } else if (!insideTag) {
      result += character;
    }
  }
  return result;
}

function extractPageTitle(body, fallbackTitle) {
  const lines = body.split('\n');
  let fence;
  for (let index = 0; index < lines.length; index += 1) {
    if (!fence) {
      fence = openCodeFence(lines[index]);
      if (fence) {
        continue;
      }
    } else if (closesCodeFence(lines[index], fence)) {
      fence = undefined;
      continue;
    }
    if (fence) {
      continue;
    }
    const heading = /^#\s+(.+?)\s*$/.exec(lines[index]);
    if (!heading) {
      continue;
    }
    lines.splice(index, 1);
    if (lines[index]?.trim().length === 0) {
      lines.splice(index, 1);
    }
    const title = heading[1]
      .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
      .replace(/[`*_]/g, '')
      .trim();
    const plainTextTitle = stripHtmlTags(title);
    return { body: lines.join('\n'), title: plainTextTitle || fallbackTitle };
  }
  return { body, title: fallbackTitle };
}

function assertNoUnconvertedConstructs(body, sourcePath) {
  const protectedLineRanges = /\[!INCLUDE|:::code/.test(body)
    ? markdownDirectiveProtectedLineRanges(body)
    : [];
  const activeBody = body
    .split('\n')
    .filter((_line, index) => !lineOverlapsRanges(index + 1, protectedLineRanges))
    .join('\n');
  const checks = [
    [/\[!INCLUDE/, 'INCLUDE', activeBody],
    [/\[!VIDEO\b/, 'VIDEO block', activeBody],
    [/\[!div\b/, 'Learn div block', activeBody],
    [/:::code\b/, 'code directive', activeBody],
    [/:::image\b/, 'image directive'],
    [/<xref:|\(xref:/, 'xref'],
  ];
  for (const [pattern, name, source = body] of checks) {
    if (pattern.test(source)) {
      throw new Error(`An unconverted ${name} remains in ${sourcePath}.`);
    }
  }
  transformOutsideCodeFences(body, (line) => {
    if (/^#{1,6}\s+\[[^\]]+\]\(#tab\/[^)]+\)\s*$/.test(line)) {
      throw new Error(`An unconverted tab group remains in ${sourcePath}.`);
    }
    return line;
  });
}

export async function convertDocfxMarkdown({
  source,
  sourcePath,
  sourceRoot = path.dirname(sourcePath),
  snippetRoots = [sourceRoot],
  includeRoot = path.dirname(sourceRoot),
  uidMap = new Map(),
  editUrl,
}) {
  const { metadata, body: originalBody, bodyStartLine } = splitFrontmatter(source);
  const metadataTitle = typeof metadata.title === 'string' ? metadata.title : inferTitle(sourcePath);
  const resolvedIncludeRoot = await realpath(includeRoot);
  let resolvedSourcePath;
  try {
    resolvedSourcePath = await realpath(sourcePath);
  } catch {
    resolvedSourcePath = path.resolve(sourcePath);
  }
  if (!isWithinRoot(resolvedIncludeRoot, resolvedSourcePath)) {
    throw new Error(`Documentation source '${resolvedSourcePath}' is outside ${resolvedIncludeRoot}.`);
  }
  let body = await expandIncludesInternal(
    originalBody,
    resolvedSourcePath,
    [resolvedSourcePath],
    resolvedIncludeRoot,
    {
      allowedRoots: snippetRoots,
      firstSourceLine: bodyStartLine,
    },
  );
  body = convertZones(body, sourcePath);
  body = await convertImages(body, sourcePath);
  body = convertLearnBlocks(body, sourcePath);
  body = convertCallouts(body, sourcePath);
  const tabs = convertTabs(body, sourcePath);
  body = tabs.source;
  body = normalizeFenceLanguages(body);
  body = transformOutsideCodeFences(body, (line) => {
    const converted = convertXrefs(convertLinks(line, sourcePath, sourceRoot), uidMap);
    return escapeMdxAngles(converted)
      .replace(/<([A-Z][A-Za-z\d_, ]*)>/g, '&lt;$1&gt;')
      .replace(/<a\s+name="([^"]+)"><\/a>/gi, '<span id="$1"></span>');
  });
  body = convertHtmlCommentsForMdx(body);
  const extractedTitle = extractPageTitle(body, metadataTitle);
  body = extractedTitle.body.trim();
  assertNoUnconvertedConstructs(body, sourcePath);
  const slug = routeFromSourcePath(sourcePath, sourceRoot)
    .replace(/^\/orleans\//, '')
    .replace(/\/$/, '');
  const componentImports = tabs.converted
    ? "import { TabItem, Tabs } from '@astrojs/starlight/components';\n\n"
    : '';
  return `${serializeFrontmatter(metadata, sourcePath, {
    title: extractedTitle.title,
    frontmatter: {
      slug,
      ...(typeof editUrl === 'string' ? { editUrl } : {}),
    },
  })}${componentImports}${body}\n`;
}

export function routeFromSourcePath(sourcePath, sourceRoot) {
  let relative = toPosix(path.relative(sourceRoot, sourcePath));
  relative = relative.slice(0, -path.extname(relative).length);
  if (path.posix.basename(relative).toLowerCase() === 'index') {
    relative = path.posix.dirname(relative);
  }
  const route = relative === '.' ? '' : relative.replace(/^\/+|\/+$/g, '');
  return `${siteBase}/${route}${route ? '/' : ''}`.replace(/\/{2,}/g, '/');
}

export async function collectUidMap(markdownFiles, sourceRoot) {
  const result = new Map();
  for (const file of markdownFiles) {
    const { metadata } = splitFrontmatter(await readFile(file, 'utf8'));
    if (typeof metadata.uid !== 'string') {
      continue;
    }
    if (result.has(metadata.uid)) {
      throw new Error(`Duplicate DocFX uid '${metadata.uid}'.`);
    }
    result.set(metadata.uid, routeFromSourcePath(file, sourceRoot));
  }
  return result;
}

function sidebarLink(href) {
  if (/^https?:\/\//.test(href) || href.startsWith('/')) {
    return href;
  }
  let target = href.replaceAll('\\', '/').replace(/\.(?:md|yml)$/i, '');
  if (path.posix.basename(target).toLowerCase() === 'index') {
    target = path.posix.dirname(target);
  }
  target = target === '.' ? '' : target.replace(/^\/+|\/+$/g, '');
  return `${contentRoot}/${target}${target ? '/' : ''}`;
}

async function sidebarItem(item, rootDirectory) {
  if (!item || typeof item.name !== 'string') {
    throw new Error('Every toc.yml item must have a name.');
  }

  if (Array.isArray(item.items)) {
    return {
      label: item.name,
      items: await Promise.all(item.items.map((child) => sidebarItem(child, rootDirectory))),
    };
  }
  if (typeof item.href !== 'string') {
    throw new Error(`toc.yml item '${item.name}' has neither items nor href.`);
  }
  if (!/^https?:\/\//.test(item.href) && !item.href.startsWith('/')) {
    const target = path.resolve(rootDirectory, item.href);
    if (!(await pathExists(target))) {
      throw new Error(`toc.yml target '${item.href}' for '${item.name}' does not exist.`);
    }
  }
  return { label: item.name, link: sidebarLink(item.href) };
}

export async function readTocItems(tocPath) {
  const resolvedPath = toPath(tocPath);
  const toc = YAML.parse(await readFile(resolvedPath, 'utf8'));
  if (!toc || !Array.isArray(toc.items)) {
    throw new Error(`${resolvedPath} does not contain a DocFX items array.`);
  }
  return toc.items[0]?.homepage || toc.items[0]?.href === 'index.yml'
    ? toc.items.slice(1)
    : toc.items;
}

export async function createSidebar(tocPath) {
  const resolvedPath = toPath(tocPath);
  const items = await readTocItems(resolvedPath);
  return Promise.all(items.map((item) => sidebarItem(item, path.dirname(resolvedPath))));
}

function hubUrl(url) {
  if (/^https?:\/\//.test(url)) {
    return url;
  }
  if (url.startsWith('/')) {
    return `https://learn.microsoft.com${url}`;
  }
  return `${deploymentBase}${sidebarLink(url)}`;
}

function renderHubCards(items, { summaryKey = 'summary', textKey = 'title' } = {}) {
  return [
    '<div class="hub-grid">',
    ...items.map((item) => {
      const title = item[textKey] ?? item.text;
      const summary = item[summaryKey] ?? item.itemType ?? '';
      return [
        `<a class="hub-card" href="${escapeHtml(hubUrl(item.url))}">`,
        `<strong>${escapeHtml(title)}</strong>`,
        summary ? `<span>${escapeHtml(summary)}</span>` : '',
        '</a>',
      ].join('');
    }),
    '</div>',
  ].join('\n');
}

export async function convertHubYaml(sourcePath) {
  const resolvedPath = toPath(sourcePath);
  const source = await readFile(resolvedPath, 'utf8');
  const match = /^### YamlMime:Hub\r?\n([\s\S]+)$/.exec(source.replace(/^\uFEFF/, ''));
  if (!match) {
    throw new Error(`${resolvedPath} is not a YamlMime:Hub document.`);
  }
  const hub = YAML.parse(match[1]);
  if (!hub || typeof hub.title !== 'string' || typeof hub.summary !== 'string') {
    throw new Error(`${resolvedPath} has an invalid YamlMime hub header.`);
  }

  const metadata = {
    mime: 'YamlMime:Hub',
    brand: hub.brand,
    ...(hub.metadata ?? {}),
  };
  const highlighted = hub.highlightedContent?.items ?? [];
  const actions = highlighted.slice(0, 2).map((item, index) => ({
    text: item.title,
    link: hubUrl(item.url),
    variant: index === 0 ? 'primary' : 'minimal',
  }));
  const frontmatter = serializeFrontmatter(metadata, resolvedPath, {
    title: hub.title,
    description: hub.summary,
    frontmatter: {
      template: 'splash',
      editUrl: false,
      hero: {
        tagline: hub.summary,
        actions,
      },
    },
  });

  const sections = [];
  if (highlighted.length > 0) {
    sections.push('## Start building', renderHubCards(highlighted));
  }

  if (hub.conceptualContent) {
    sections.push(`## ${hub.conceptualContent.title}`);
    if (hub.conceptualContent.summary) {
      sections.push(hub.conceptualContent.summary);
    }
    for (const group of hub.conceptualContent.items ?? []) {
      sections.push(`### ${group.title}`, renderHubCards(group.links ?? [], { textKey: 'text' }));
    }
  }

  for (const section of hub.additionalContent?.sections ?? []) {
    sections.push(`## ${section.title}`);
    if (section.summary) {
      sections.push(section.summary);
    }
    for (const group of section.items ?? []) {
      sections.push(`### ${group.title}`);
      if (group.summary) {
        sections.push(group.summary);
      }
      if (group.url) {
        sections.push(renderHubCards([group]));
      }
      if (Array.isArray(group.links)) {
        sections.push(renderHubCards(group.links, { textKey: 'text' }));
      }
    }
  }

  if (hub.additionalContent?.footer) {
    sections.push(
      '---',
      convertLinks(hub.additionalContent.footer, resolvedPath, path.dirname(resolvedPath)),
    );
  }

  return `${frontmatter}${sections.join('\n\n').trim()}\n`;
}
