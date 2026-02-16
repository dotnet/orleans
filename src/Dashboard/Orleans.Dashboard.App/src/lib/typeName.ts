export function getName(value: string): string {
  // Parse a type name and shorten fully-qualified names to their last segment.
  // Handles generic type arguments (angle brackets or square brackets) and
  // shortens their content recursively. Examples:
  // "A.B.C<T.U.V>" -> "C<V>"
  // "G<X.Y<Z.W>>" -> "G<Y<W>>"
  function trimIdentifier(id: string): string {
    if (!id) return id;
    id = id.trim();
    // Remove assembly details if present: "Type, Assembly"
    const comma = id.indexOf(',');
    if (comma !== -1) id = id.substring(0, comma).trim();
    const parts = id.split('.');
    const last = parts[parts.length - 1];
    const tickIndex = last.indexOf('`');
    return tickIndex !== -1 ? last.substring(0, tickIndex) : last;
  }

  function parseType(str: string): string {
    if (!str) return str;
    let s = str.trim();

    // Find first generic opener: '<' or '['
    const lt = s.indexOf('<');
    const lb = s.indexOf('[');
    let opener = '';
    let openerPos = -1;
    let closer = '';
    if (lt !== -1 && (lb === -1 || lt < lb)) {
      opener = '<'; closer = '>'; openerPos = lt;
    } else if (lb !== -1) {
      opener = '['; closer = ']'; openerPos = lb;
    }

    if (openerPos === -1) {
      return trimIdentifier(s);
    }

    const main = s.substring(0, openerPos);

    // Find matching closer for the opener, honoring nested pairs
    let depth = 0;
    let end = -1;
    for (let i = openerPos; i < s.length; i++) {
      const ch = s[i];
      if (ch === opener) depth++; else if (ch === closer) {
        depth--; if (depth === 0) { end = i; break; }
      }
    }

    if (end === -1) {
      return trimIdentifier(s);
    }

    const inner = s.substring(openerPos + 1, end);
    const remainder = s.substring(end + 1);

    // Split inner by top-level commas only
    const args: string[] = [];
    let argStart = 0;
    depth = 0;
    for (let i = 0; i < inner.length; i++) {
      const ch = inner[i];
      if (ch === '<' || ch === '[') depth++; else if (ch === '>' || ch === ']') depth--;
      else if (ch === ',' && depth === 0) {
        args.push(inner.substring(argStart, i));
        argStart = i + 1;
      }
    }
    args.push(inner.substring(argStart));

    const parsed = args.map(a => parseType(a));

    // If there is a remainder after the generic (e.g. ".Method"),
    // preserve it while shortening each dotted segment inside it.
    function parseSuffix(rem: string): string {
      if (!rem) return '';
      let i = 0;
      // accept leading dots/spaces
      while (i < rem.length && (rem[i] === '.' || rem[i] === ' ')) i++;
      if (i === 0) return rem; // unexpected format, return raw

      const segments: string[] = [];
      let segStart = i;
      let depth = 0;
      for (let j = i; j < rem.length; j++) {
        const ch = rem[j];
        if (ch === '<' || ch === '[') depth++; else if (ch === '>' || ch === ']') depth--;
        else if (ch === '.' && depth === 0) {
          segments.push(rem.substring(segStart, j));
          segStart = j + 1;
        }
      }
      if (segStart <= rem.length) segments.push(rem.substring(segStart));

      const parsedSegs = segments.map(seg => parseType(seg));
      return '.' + parsedSegs.join('.');
    }

    return `${trimIdentifier(main)}<${parsed.join(', ')}>${parseSuffix(remainder)}`;
  }

  return parseType(value);
}
