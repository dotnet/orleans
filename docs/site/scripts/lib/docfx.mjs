import { access, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const markdownExtensions = new Set(['.md', '.markdown', '.mdown', '.mkdn', '.mkd', '.mdwn']);
const directiveAttributePattern = /([\w-]+)="([^"]*)"/g;
const learnDocsPrefix = '/dotnet/orleans';
const contentRoot = '/docs';
const deploymentBase = '/orleans';
const siteBase = `${deploymentBase}${contentRoot}`;

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

function toPosix(value) {
  return value.split(path.sep).join('/');
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
    return true;
  } catch {
    return false;
  }
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
    return { metadata: {}, body: normalized };
  }

  const metadata = YAML.parse(match[1]) ?? {};
  if (typeof metadata !== 'object' || Array.isArray(metadata)) {
    throw new Error('DocFX frontmatter must be a YAML object.');
  }

  return {
    metadata,
    body: normalized.slice(match[0].length),
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

async function expandIncludesInternal(source, sourcePath, stack) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const output = [];

  for (const line of lines) {
    const match = /^(\s*)\[!INCLUDE\s+\[([^\]]+)\]\(([^)]+)\)\]\s*$/.exec(line);
    if (!match) {
      if (line.includes('[!INCLUDE')) {
        throw new Error(`Unsupported INCLUDE syntax in ${sourcePath}: ${line.trim()}`);
      }
      output.push(line);
      continue;
    }

    const [, indent, label, relativePath] = match;
    const includePath = path.resolve(path.dirname(sourcePath), relativePath);
    if (!(await pathExists(includePath))) {
      throw new Error(`INCLUDE '${relativePath}' in ${sourcePath} does not exist (${includePath}).`);
    }
    if (stack.includes(includePath)) {
      throw new Error(`Circular INCLUDE detected: ${[...stack, includePath].join(' -> ')}`);
    }

    const includeSource = await readFile(includePath, 'utf8');
    const { body } = splitFrontmatter(includeSource);
    const expanded = rebaseIncludedReferences(
      await expandIncludesInternal(body, includePath, [...stack, includePath]),
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

export async function expandIncludes(source, sourcePath) {
  return expandIncludesInternal(source, path.resolve(sourcePath), [path.resolve(sourcePath)]);
}

export async function collectIncludeTargets(markdownFiles) {
  const targets = new Set();
  const visited = new Set();

  async function collect(filePath) {
    const resolvedPath = path.resolve(filePath);
    if (visited.has(resolvedPath)) {
      return;
    }
    visited.add(resolvedPath);

    const source = await readFile(resolvedPath, 'utf8');
    const { body } = splitFrontmatter(source);
    for (const line of body.replaceAll('\r\n', '\n').split('\n')) {
      const match = /^\s*\[!INCLUDE\s+\[[^\]]+\]\(([^)]+)\)\]\s*$/.exec(line);
      if (!match) {
        if (line.includes('[!INCLUDE')) {
          throw new Error(`Unsupported INCLUDE syntax in ${resolvedPath}: ${line.trim()}`);
        }
        continue;
      }

      const target = path.resolve(path.dirname(resolvedPath), match[1]);
      if (!(await pathExists(target))) {
        throw new Error(`INCLUDE '${match[1]}' in ${resolvedPath} does not exist (${target}).`);
      }
      targets.add(target);
      await collect(target);
    }
  }

  for (const file of markdownFiles) {
    await collect(file);
  }
  return targets;
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

async function convertCodeDirectives(source, sourcePath) {
  const output = [];
  for (const line of source.replaceAll('\r\n', '\n').split('\n')) {
    const match = /^(\s*):::code\s+(.+?)\s*$/.exec(line);
    if (!match) {
      if (line.includes(':::code')) {
        throw new Error(`Unsupported code directive in ${sourcePath}: ${line.trim()}`);
      }
      output.push(line);
      continue;
    }

    const [, indent, rawAttributeSource] = match;
    const attributeSource = rawAttributeSource.endsWith(':::')
      ? rawAttributeSource.slice(0, -3).trimEnd()
      : rawAttributeSource;
    const attributes = parseDirectiveAttributes(attributeSource, `code directive in ${sourcePath}`);
    const unknown = Object.keys(attributes).filter(
      (key) => !['highlight', 'id', 'language', 'range', 'source'].includes(key),
    );
    if (unknown.length > 0) {
      throw new Error(`Unsupported code attributes '${unknown.join(', ')}' in ${sourcePath}.`);
    }
    if (!attributes.source) {
      throw new Error(`A code directive in ${sourcePath} is missing its source attribute.`);
    }

    const snippetPath = path.resolve(path.dirname(sourcePath), attributes.source);
    if (!(await pathExists(snippetPath))) {
      throw new Error(
        `Code source '${attributes.source}' in ${sourcePath} does not exist (${snippetPath}).`,
      );
    }

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
  const lines = source.split('\n');
  const output = [];
  const kinds = {
    CAUTION: ['danger', 'Caution'],
    IMPORTANT: ['note', 'Important'],
    NOTE: ['note', 'Note'],
    TIP: ['tip', 'Tip'],
    WARNING: ['caution', 'Warning'],
  };

  for (let index = 0; index < lines.length; index += 1) {
    const match = /^(\s*)>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/.exec(lines[index]);
    if (!match) {
      if (/>\s*\[![A-Z]+\]/.test(lines[index])) {
        throw new Error(`Unsupported callout in ${sourcePath}: ${lines[index].trim()}`);
      }
      output.push(lines[index]);
      continue;
    }

    const [, indent, kind] = match;
    const [variant, title] = kinds[kind];
    const body = [];
    while (index + 1 < lines.length) {
      const contentMatch = new RegExp(`^${escapeRegExp(indent)}> ?(.*)$`).exec(lines[index + 1]);
      if (!contentMatch) {
        break;
      }
      body.push(contentMatch[1]);
      index += 1;
    }
    output.push(`${indent}:::${variant}[${title}]`);
    output.push(...body.map((bodyLine) => `${indent}${bodyLine}`));
    output.push(`${indent}:::`);
  }

  return output.join('\n');
}

function convertLearnBlocks(source, sourcePath) {
  const lines = source.split('\n');
  const output = [];

  for (let index = 0; index < lines.length; index += 1) {
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
    const body = [];
    while (index + 1 < lines.length) {
      const content = new RegExp(`^${escapeRegExp(indent)}> ?(.*)$`).exec(lines[index + 1]);
      if (!content) {
        break;
      }
      body.push(content[1]);
      index += 1;
    }

    if (className === 'nextstepaction') {
      output.push(`${indent}:::tip[Next step]`, ...body.map((line) => `${indent}${line}`), `${indent}:::`);
    } else if (className === 'checklist') {
      output.push(...body.map((line) => `${indent}${line}`));
    } else {
      throw new Error(`Unsupported Learn div class '${className}' in ${sourcePath}.`);
    }
  }

  return output.join('\n');
}

function convertImages(source, sourcePath) {
  return source
    .split('\n')
    .map((line) => {
      const match = /^(\s*):::image\s+(.+?):::\s*$/.exec(line);
      if (!match) {
        if (line.includes(':::image')) {
          throw new Error(`Unsupported image directive in ${sourcePath}: ${line.trim()}`);
        }
        return line;
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

      const alt = escapeMarkdown(attributes['alt-text']);
      const image = `![${alt}](${attributes.source})`;
      return `${indent}${image}`;
    })
    .join('\n');
}

function formatPivot(pivot) {
  const match = /^orleans-(\d+)-(x|\d+)$/.exec(pivot);
  if (!match) {
    throw new Error(`Unsupported Orleans version pivot '${pivot}'.`);
  }
  return `Orleans ${match[1]}.${match[2]}`;
}

function convertZones(source, sourcePath) {
  const output = [];
  const stack = [];
  for (const line of source.split('\n')) {
    const start = /^(\s*):::zone\s+(.+?)\s*$/.exec(line);
    if (start) {
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

    const end = /^(\s*):::zone-end\s*$/.exec(line);
    if (end) {
      if (stack.length === 0) {
        throw new Error(`Unmatched zone-end in ${sourcePath}.`);
      }
      const indent = stack.pop();
      output.push(`${indent}::::`);
      continue;
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

function convertXrefs(line, uidMap) {
  let converted = line.replace(
    /\[((?:\\.|`[^`]*`|[^\]])+)\]\((?:<)?xref:([^)>]+)>?\)/g,
    (_match, label, reference) => {
      const [uid] = reference.split('?');
      const normalizedLabel = label.replace(/\\</g, '&lt;').replace(/\\>/g, '&gt;');
      return `[${normalizedLabel}](${xrefUrl(uid, uidMap)})`;
    },
  );
  converted = converted.replace(
    /\[([^\]]*<xref:[^>]+>[^\]]*)\]\(([^)]+)\)/g,
    (_match, label, target) => {
      const plainLabel = label.replace(/<xref:([^>]+)>/g, (_xref, reference) => {
        const [uid, query = ''] = reference.split('?');
        return escapeMarkdown(
          humanizeXref(uid, new URLSearchParams(query).get('displayProperty')),
        );
      });
      return `[${plainLabel}](${target})`;
    },
  );
  converted = converted.replace(
    /\[([^\]]+)\]\(xref:([^)]+)\)/g,
    (_, label, reference) => {
      const [uid] = reference.split('?');
      return `[${label}](${xrefUrl(uid, uidMap)})`;
    },
  );
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
  return line.replace(
    /(?<!!)\[([^\]]+)\]\(([^)\s]+)(?:\s+"([^"]*)")?\)/g,
    (_match, label, target, title) => {
      const convertedTarget = convertLinkTarget(target, sourcePath, sourceRoot);
      return `[${label}](${convertedTarget}${title ? ` "${title}"` : ''})`;
    },
  );
}

function transformOutsideCodeFences(source, transform) {
  let fence;
  return source
    .split('\n')
    .map((line) => {
      const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
      if (marker) {
        if (!fence) {
          fence = marker[0];
        } else if (marker[0] === fence) {
          fence = undefined;
        }
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
    const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
    if (marker && !fence) {
      flush(false);
      fence = marker[0];
    }
    buffer.push(line);
    if (marker && fence && marker[0] === fence && buffer.length > 1) {
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
    'a|abbr|b|blockquote|br|code|dd|details|div|dl|dt|em|hr|i|iframe|img|input|kbd|li|ol|p|pre|source|span|strong|sub|summary|sup|table|tbody|td|th|thead|tr|ul';
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

function removeDuplicateTitle(body, title) {
  const lines = body.split('\n');
  const firstContent = lines.findIndex((line) => line.trim().length > 0);
  if (firstContent < 0) {
    return body;
  }
  const heading = /^#\s+(.+?)\s*$/.exec(lines[firstContent]);
  if (
    heading &&
    heading[1].replaceAll('`', '').trim().toLowerCase() === title.replaceAll('`', '').trim().toLowerCase()
  ) {
    lines.splice(firstContent, 1);
    if (lines[firstContent]?.trim().length === 0) {
      lines.splice(firstContent, 1);
    }
  }
  return lines.join('\n');
}

function assertNoUnconvertedConstructs(body, sourcePath) {
  const checks = [
    [/\[!INCLUDE/, 'INCLUDE'],
    [/\[!VIDEO\b/, 'VIDEO block'],
    [/\[!div\b/, 'Learn div block'],
    [/:::code\b/, 'code directive'],
    [/:::image\b/, 'image directive'],
    [/:::zone(?:-end)?\b/, 'version zone'],
    [/<xref:|\(xref:/, 'xref'],
  ];
  for (const [pattern, name] of checks) {
    if (pattern.test(body)) {
      throw new Error(`An unconverted ${name} remains in ${sourcePath}.`);
    }
  }
}

export async function convertDocfxMarkdown({
  source,
  sourcePath,
  sourceRoot = path.dirname(sourcePath),
  uidMap = new Map(),
  editUrl,
}) {
  const { metadata, body: originalBody } = splitFrontmatter(source);
  const title = typeof metadata.title === 'string' ? metadata.title : inferTitle(sourcePath);
  let body = await expandIncludes(originalBody, sourcePath);
  body = await convertCodeDirectives(body, sourcePath);
  body = convertImages(body, sourcePath);
  body = convertLearnBlocks(body, sourcePath);
  body = convertCallouts(body, sourcePath);
  body = convertZones(body, sourcePath);
  body = body.replace(/^#{1,6}\s+\[([^\]]+)\]\(#tab\/[^)]+\)\s*$/gm, '### $1');
  body = normalizeFenceLanguages(body);
  body = transformOutsideCodeFences(body, (line) => {
    const converted = convertLinks(convertXrefs(line, uidMap), sourcePath, sourceRoot);
    return escapeMdxAngles(converted)
      .replace(/<([A-Z][A-Za-z\d_, ]*)>/g, '&lt;$1&gt;')
      .replace(/<a\s+name="([^"]+)"><\/a>/gi, '<span id="$1"></span>');
  });
  body = convertHtmlCommentsForMdx(body);
  body = removeDuplicateTitle(body, title).trim();
  assertNoUnconvertedConstructs(body, sourcePath);
  const slug = routeFromSourcePath(sourcePath, sourceRoot)
    .replace(/^\/orleans\//, '')
    .replace(/\/$/, '');
  return `${serializeFrontmatter(metadata, sourcePath, {
    frontmatter: {
      slug,
      ...(typeof editUrl === 'string' ? { editUrl } : {}),
    },
  })}${body}\n`;
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
  if (/^https?:\/\//.test(href)) {
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
  if (!/^https?:\/\//.test(item.href)) {
    const target = path.resolve(rootDirectory, item.href);
    if (!(await pathExists(target))) {
      throw new Error(`toc.yml target '${item.href}' for '${item.name}' does not exist.`);
    }
  }
  return { label: item.name, link: sidebarLink(item.href) };
}

export async function createSidebar(tocPath) {
  const resolvedPath = toPath(tocPath);
  const toc = YAML.parse(await readFile(resolvedPath, 'utf8'));
  if (!toc || !Array.isArray(toc.items)) {
    throw new Error(`${resolvedPath} does not contain a DocFX items array.`);
  }
  const items =
    toc.items[0]?.homepage || toc.items[0]?.href === 'index.yml' ? toc.items.slice(1) : toc.items;
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
