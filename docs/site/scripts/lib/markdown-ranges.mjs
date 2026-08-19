let fromMarkdown;
try {
  ({ fromMarkdown } = await import('mdast-util-from-markdown'));
} catch (error) {
  if (error?.code !== 'ERR_MODULE_NOT_FOUND') {
    throw error;
  }
}

const directiveProtectedTypes = new Set(['blockquote', 'code', 'html', 'inlineCode']);
const htmlBlockTags = new Set([
  'address',
  'article',
  'aside',
  'base',
  'basefont',
  'blockquote',
  'body',
  'caption',
  'center',
  'col',
  'colgroup',
  'dd',
  'details',
  'dialog',
  'dir',
  'div',
  'dl',
  'dt',
  'fieldset',
  'figcaption',
  'figure',
  'footer',
  'form',
  'frame',
  'frameset',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'head',
  'header',
  'hr',
  'html',
  'iframe',
  'legend',
  'li',
  'link',
  'main',
  'menu',
  'menuitem',
  'nav',
  'noframes',
  'ol',
  'optgroup',
  'option',
  'p',
  'param',
  'search',
  'section',
  'summary',
  'table',
  'tbody',
  'td',
  'tfoot',
  'th',
  'thead',
  'title',
  'tr',
  'track',
  'ul',
]);

function mergeLines(lines) {
  const sorted = [...lines].sort((left, right) => left - right);
  const ranges = [];
  for (const line of sorted) {
    const current = ranges.at(-1);
    if (current && line <= current[1] + 1) {
      current[1] = Math.max(current[1], line);
    } else {
      ranges.push([line, line]);
    }
  }
  return ranges;
}

function expandLeadingTabs(line) {
  let columns = 0;
  let index = 0;
  while (index < line.length) {
    if (line[index] === ' ') {
      columns += 1;
    } else if (line[index] === '\t') {
      columns += 4 - (columns % 4);
    } else {
      break;
    }
    index += 1;
  }
  return `${' '.repeat(columns)}${line.slice(index)}`;
}

function listContentIndent(lines, index, indentation) {
  for (let candidate = index - 1; candidate >= 0; candidate -= 1) {
    const candidateLine = expandLeadingTabs(lines[candidate]);
    if (candidateLine.trim().length === 0) {
      continue;
    }
    const match = /^( {0,3})(?:[-+*]|\d+[.)])([ \t]+)\S/.exec(candidateLine);
    if (match && match[0].length - 1 < indentation) {
      return match[1].length + match[0].length - match[1].length - 1;
    }
    if (/^ */.exec(candidateLine)[0].length < indentation) {
      return undefined;
    }
  }
  return undefined;
}

function fenceOpening(lines, index) {
  let line = expandLeadingTabs(lines[index]);
  let containerIndentation = 0;
  const sameLineList = /^( {0,3})(?:[-+*]|\d+[.)])([ \t]+)(.*)$/.exec(line);
  if (sameLineList) {
    containerIndentation = line.length - sameLineList[3].length;
    line = sameLineList[3];
  }
  const match = /^( *)(`{3,}|~{3,})(.*)$/.exec(line);
  if (!match || (match[2][0] === '`' && match[3].includes('`'))) {
    return undefined;
  }
  const indentation = match[1].length;
  if (!sameLineList && indentation > 3) {
    const listIndentation = listContentIndent(lines, index, indentation);
    if (listIndentation === undefined || indentation > listIndentation + 3) {
      return undefined;
    }
    containerIndentation = listIndentation;
  }
  return {
    character: match[2][0],
    length: match[2].length,
    indentation: containerIndentation,
  };
}

function closesFence(line, fence) {
  line = expandLeadingTabs(line);
  const marker = fence.character === '`' ? '`' : '~';
  const match = new RegExp(`^( *)${marker}{${fence.length},}[ \\t]*$`).exec(line);
  if (!match) {
    return false;
  }
  const indentation = match[1].length;
  return indentation >= fence.indentation && indentation <= fence.indentation + 3;
}

function hasInlineCode(line) {
  for (let index = 0; index < line.length; index += 1) {
    if (line[index] === '\\') {
      index += 1;
      continue;
    }
    if (line[index] !== '`') {
      continue;
    }
    let end = index + 1;
    while (line[end] === '`') {
      end += 1;
    }
    const delimiter = '`'.repeat(end - index);
    if (line.indexOf(delimiter, end) >= 0) {
      return true;
    }
    index = end - 1;
  }
  return false;
}

function startsHtmlBlock(line) {
  const trimmed = line.trimStart();
  if (
    trimmed.startsWith('<!--') ||
    trimmed.startsWith('<?') ||
    trimmed.startsWith('<![CDATA[') ||
    /^<![A-Z]/.test(trimmed)
  ) {
    return {
      close: trimmed.startsWith('<!--')
        ? '-->'
        : trimmed.startsWith('<![CDATA[')
          ? ']]>'
          : trimmed.startsWith('<?')
            ? '?>'
            : '>',
    };
  }
  const rawTag = /^<(pre|script|style|textarea)(?:\s|>|$)/i.exec(trimmed);
  if (rawTag) {
    return { closePattern: new RegExp(`<\\/\\s*${rawTag[1]}\\s*>`, 'i') };
  }
  const blockTag = /^<\/?([a-z][a-z\d-]*)(?:\s|\/?>|$)/i.exec(trimmed);
  if (blockTag && htmlBlockTags.has(blockTag[1].toLowerCase())) {
    return { untilBlank: true };
  }
  if (!/^<\/?[a-z][a-z\d-]*/i.test(trimmed)) {
    return undefined;
  }
  let quote;
  let tagEnd = -1;
  for (let index = 1; index < trimmed.length; index += 1) {
    const character = trimmed[index];
    if (quote) {
      if (character === quote) {
        quote = undefined;
      }
    } else if (character === '"' || character === "'") {
      quote = character;
    } else if (character === '>') {
      tagEnd = index;
      break;
    }
  }
  return tagEnd >= 0 && trimmed.slice(tagEnd + 1).trim().length === 0
    ? { untilBlank: true }
    : undefined;
}

function startsBlockConstruct(content) {
  return /^(?:#{1,6}(?:\s|$)|(?:[-+*]|\d+[.)])\s|`{3,}|~{3,}|>|<|(?:[-*_]\s*){3,}$)/.test(
    content.trimStart(),
  );
}

function fallbackProtectedLineRanges(source) {
  const lines = source.replaceAll('\r\n', '\n').split('\n');
  const protectedLines = new Set();
  let fence;
  let html;
  let lazyBlockquote = false;
  for (let index = 0; index < lines.length; index += 1) {
    const lineNumber = index + 1;
    const line = lines[index];
    if (fence) {
      protectedLines.add(lineNumber);
      if (closesFence(line, fence)) {
        fence = undefined;
      }
      continue;
    }
    if (html) {
      if (html.untilBlank && line.trim().length === 0) {
        html = undefined;
      } else {
        protectedLines.add(lineNumber);
        if (
          (html.close && line.includes(html.close)) ||
          (html.closePattern && html.closePattern.test(line))
        ) {
          html = undefined;
        }
      }
      continue;
    }
    const blockquote = /^ {0,3}>[ \t]?(.*)$/.exec(line);
    if (blockquote) {
      protectedLines.add(lineNumber);
      lazyBlockquote =
        blockquote[1].trim().length > 0 && !startsBlockConstruct(blockquote[1]);
      continue;
    }
    if (lazyBlockquote) {
      if (line.trim().length === 0) {
        lazyBlockquote = false;
      } else if (
        !/^ {0,3}(?:#{1,6}(?:\s|$)|(?:[-+*]|\d+[.)])\s|`{3,}|~{3,}|<)/.test(line)
      ) {
        protectedLines.add(lineNumber);
        continue;
      }
      lazyBlockquote = false;
    }
    const opening = fenceOpening(lines, index);
    if (opening) {
      fence = opening;
      protectedLines.add(lineNumber);
      continue;
    }
    const indentation = /^ */.exec(expandLeadingTabs(line))[0].length;
    const contentIndentation = listContentIndent(lines, index, indentation);
    if (
      indentation >= 4 &&
      (contentIndentation === undefined || indentation >= contentIndentation + 4)
    ) {
      protectedLines.add(lineNumber);
      continue;
    }
    const htmlOpening = startsHtmlBlock(line);
    if (htmlOpening) {
      protectedLines.add(lineNumber);
      if (
        !(
          (htmlOpening.close && line.includes(htmlOpening.close)) ||
          (htmlOpening.closePattern && htmlOpening.closePattern.test(line))
        )
      ) {
        html = htmlOpening;
      }
      continue;
    }
    if (hasInlineCode(line)) {
      protectedLines.add(lineNumber);
    }
  }
  return mergeLines(protectedLines);
}

export function markdownDirectiveProtectedLineRanges(source, options = {}) {
  if (!fromMarkdown || options.dependencyFree) {
    return fallbackProtectedLineRanges(source);
  }
  return markdownAstLineRanges(source, directiveProtectedTypes);
}

function markdownAstLineRanges(source, acceptedTypes) {
  const ranges = [];
  const pending = [fromMarkdown(source)];
  while (pending.length > 0) {
    const node = pending.pop();
    if (acceptedTypes.has(node.type)) {
      ranges.push([node.position.start.line, node.position.end.line]);
    }
    if (Array.isArray(node.children)) {
      pending.push(...node.children);
    }
  }
  return ranges;
}

function markdownAstOffsetRanges(source, acceptedTypes) {
  const ranges = [];
  const pending = [fromMarkdown(source)];
  while (pending.length > 0) {
    const node = pending.pop();
    if (
      acceptedTypes.has(node.type) &&
      Number.isInteger(node.position?.start.offset) &&
      Number.isInteger(node.position?.end.offset)
    ) {
      ranges.push([node.position.start.offset, node.position.end.offset]);
    }
    if (Array.isArray(node.children)) {
      pending.push(...node.children);
    }
  }
  return ranges.sort((left, right) => left[0] - right[0]);
}

export function markdownBlockquoteLineRanges(source) {
  return fromMarkdown
    ? markdownAstLineRanges(source, new Set(['blockquote']))
    : fallbackProtectedLineRanges(source);
}

export function markdownLiteralLineRanges(source) {
  return fromMarkdown
    ? markdownAstLineRanges(source, new Set(['code', 'html', 'inlineCode']))
    : fallbackProtectedLineRanges(source);
}

export function markdownCodeOffsetRanges(source, options = {}) {
  return fromMarkdown && !options.dependencyFree
    ? markdownAstOffsetRanges(source, new Set(['code', 'inlineCode']))
    : undefined;
}

export function lineOverlapsRanges(line, ranges) {
  return ranges.some(([start, end]) => start <= line && line <= end);
}
