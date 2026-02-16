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
    return parts[parts.length - 1];
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
    return `${trimIdentifier(main)}<${parsed.join(', ')}>`;
  }

  return parseType(value);
}
