function trimIdentifier(id: string): string {
  if (!id) return id;
  const trimmed = stripAssemblyDetails(id.trim());
  const parts = trimmed.split('.');
  const last = parts[parts.length - 1];
  const tickIndex = last.indexOf('`');
  return tickIndex !== -1 ? last.substring(0, tickIndex) : last;
}

function stripAssemblyDetails(value: string): string {
  let depth = 0;
  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    if (ch === '<' || ch === '[') depth++;
    else if (ch === '>' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) return value.substring(0, i).trim();
  }

  return value.trim();
}

function findMatchingCloser(value: string, start: number, opener: string, closer: string): number {
  let depth = 0;
  for (let i = start; i < value.length; i++) {
    const ch = value[i];
    if (ch === opener) depth++;
    else if (ch === closer) {
      depth--;
      if (depth === 0) return i;
    }
  }

  return -1;
}

function splitTopLevelArguments(value: string): string[] {
  const args: string[] = [];
  let argStart = 0;
  let depth = 0;

  for (let i = 0; i < value.length; i++) {
    const ch = value[i];
    if (ch === '<' || ch === '[') depth++;
    else if (ch === '>' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) {
      args.push(value.substring(argStart, i));
      argStart = i + 1;
    }
  }

  args.push(value.substring(argStart));
  return args;
}

function parseSuffix(rem: string): string {
  if (!rem) return '';
  let i = 0;
  while (i < rem.length && (rem[i] === '.' || rem[i] === ' ')) i++;
  if (i === 0) return rem;

  const segments: string[] = [];
  let segStart = i;
  let depth = 0;
  for (let j = i; j < rem.length; j++) {
    const ch = rem[j];
    if (ch === '<' || ch === '[') depth++;
    else if (ch === '>' || ch === ']') depth--;
    else if (ch === '.' && depth === 0) {
      segments.push(rem.substring(segStart, j));
      segStart = j + 1;
    }
  }

  if (segStart <= rem.length) segments.push(rem.substring(segStart));

  return '.' + segments.map(seg => parseType(seg)).join('.');
}

function parseType(str: string): string {
  if (!str) return str;
  const s = stripAssemblyDetails(str.trim());
  if (!s) return s;

  // Handle assembly-qualified generic arguments wrapped in [ ... ].
  if (s[0] === '[') {
    const wrappedEnd = findMatchingCloser(s, 0, '[', ']');
    if (wrappedEnd === s.length - 1) {
      return parseType(stripAssemblyDetails(s.substring(1, wrappedEnd)));
    }
  }

  // Find first generic opener: '<' or '['
  const lt = s.indexOf('<');
  const lb = s.indexOf('[');
  let opener = '';
  let openerPos = -1;
  let closer = '';
  if (lt !== -1 && (lb === -1 || lt < lb)) {
    opener = '<';
    closer = '>';
    openerPos = lt;
  } else if (lb !== -1) {
    opener = '[';
    closer = ']';
    openerPos = lb;
  }

  if (openerPos === -1) {
    return trimIdentifier(s);
  }

  const main = s.substring(0, openerPos);
  const end = findMatchingCloser(s, openerPos, opener, closer);

  if (end === -1) {
    return trimIdentifier(s);
  }

  const inner = s.substring(openerPos + 1, end);
  const remainder = s.substring(end + 1);
  const parsed = splitTopLevelArguments(inner).map(arg => parseType(arg.trim()));

  return `${trimIdentifier(main)}<${parsed.join(', ')}>${parseSuffix(remainder)}`;
}

export function getName(value: string): string {
  return parseType(value);
}
