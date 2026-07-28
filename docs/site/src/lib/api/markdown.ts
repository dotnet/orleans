import {
  buildTypeSignature,
  buildMemberSignature,
  findTypeByDocId,
  groupMembersByKind,
  groupTypesByNamespace,
  memberDisplayName,
  memberKindLabels,
  memberKindPath,
  memberPath,
  memberSlug,
  nugetHref,
  packagePath,
  shortTypeName,
  sourceHref,
  typeDisplayName,
  typePath,
  withBase,
} from './packages';
import type {
  ApiDocNode,
  ApiDocumentation,
  ApiMember,
  ApiType,
  MemberKind,
  PackageApiDocument,
} from './types';

interface MarkdownContext {
  allTypes: ApiType[];
  packageName: string;
  base: string;
}

function markdownText(value: string): string {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}

export function renderApiIndexMarkdown(
  packages: PackageApiDocument[],
  base = '/',
): string {
  const content =
    packages.length === 0
      ? 'No API package metadata has been generated yet.'
      : packages
          .map(
            (pkg) =>
              `- [${markdownText(pkg.package.name)}](${withBase(base, packagePath(pkg.package.name))}) ${pkg.package.version} (${pkg.package.targetFramework})`,
          )
          .join('\n');
  return finalizeMarkdown([
    '# .NET Orleans API reference',
    'Browse the API surface generated directly from Orleans assemblies, XML documentation, and source information.',
    content,
  ]);
}

export function renderPackageMarkdown(pkg: PackageApiDocument, base = '/'): string {
  const namespaces = groupTypesByNamespace(pkg.types);
  const context = { allTypes: pkg.types, packageName: pkg.package.name, base };
  const groups = [...namespaces.entries()].map(([namespace, types]) =>
    section(
      namespace,
      types
        .map((type) => {
          const summary = inlineSummary(type.docs, context);
          return `- [${markdownText(typeDisplayName(type))}](${withBase(base, typePath(pkg.package.name, type))})${summary ? ` - ${summary}` : ''}`;
        })
        .join('\n'),
    ),
  );
  return finalizeMarkdown([
    `# ${pkg.package.name}`,
    `${pkg.package.version} | ${pkg.package.targetFramework}`,
    `[NuGet package](${nugetHref(pkg.package.name)})`,
    ...groups,
  ]);
}

export function renderTypeMarkdown(
  pkg: PackageApiDocument,
  type: ApiType,
  base = '/',
): string {
  const context = { allTypes: pkg.types, packageName: pkg.package.name, base };
  const source = sourceHref(pkg.package, type.sourceFile, type.sourceLines);
  const memberGroups = [...groupMembersByKind(type.members).entries()].map(([kind, members]) =>
    section(
      memberKindLabels[kind],
      members
        .map(
          (member) =>
            `- [${markdownText(memberDisplayName(member, type))}](${withBase(base, memberPath(pkg.package.name, type, member))})`,
        )
        .join('\n'),
    ),
  );
  const enumMembers = type.enumMembers?.length
    ? section(
        'Fields',
        type.enumMembers
          .map(
            (member) =>
              `- \`${member.name}\` = \`${member.value}\`${member.description ? ` - ${markdownText(member.description)}` : ''}`,
          )
          .join('\n'),
      )
    : '';
  return finalizeMarkdown([
    `# ${markdownText(typeDisplayName(type))}`,
    `Package: [${pkg.package.name}](${withBase(base, packagePath(pkg.package.name))}) ${pkg.package.version}`,
    `[NuGet package](${nugetHref(pkg.package.name)})${source ? ` | [Source](${source})` : ''}`,
    codeBlock(buildTypeSignature(type), 'csharp'),
    renderDocumentation(type.docs, context),
    enumMembers,
    ...memberGroups,
  ]);
}

export function renderMemberKindMarkdown(
  pkg: PackageApiDocument,
  type: ApiType,
  kind: MemberKind,
  base = '/',
): string {
  const members = (type.members ?? []).filter((member) => member.kind === kind);
  const context = { allTypes: pkg.types, packageName: pkg.package.name, base };
  return finalizeMarkdown([
    `# ${markdownText(typeDisplayName(type))} ${memberKindLabels[kind]}`,
    `[Type overview](${withBase(base, typePath(pkg.package.name, type))})`,
    ...members.map((member) =>
      renderMemberSectionMarkdown(pkg, type, member, context),
    ),
  ]);
}

export function renderMemberMarkdown(
  pkg: PackageApiDocument,
  type: ApiType,
  member: ApiMember,
  base = '/',
): string {
  const context = { allTypes: pkg.types, packageName: pkg.package.name, base };
  return finalizeMarkdown([
    `# ${markdownText(typeDisplayName(type))}.${markdownText(memberDisplayName(member, type))}`,
    `[Type overview](${withBase(base, typePath(pkg.package.name, type))}) | [${memberKindLabels[member.kind]}](${withBase(base, memberKindPath(pkg.package.name, type, member.kind))})`,
    renderMemberSectionMarkdown(pkg, type, member, context, false),
  ]);
}

export function markdownResponse(content: string): Response {
  return new Response(content, {
    headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
  });
}

export function renderDocNodes(
  content: ApiDocNode[] | string | undefined,
  context: MarkdownContext,
): string {
  if (!content) return '';
  if (typeof content === 'string') return content.trim();
  return content.map((node) => renderDocNode(node, context)).join('').trim();
}

function renderDocNode(node: ApiDocNode, context: MarkdownContext): string {
  switch (node.kind) {
    case 'text':
      return markdownText(node.text ?? '');
    case 'code':
      return inlineCode(node.text ?? '');
    case 'codeblock':
      return `\n\n${codeBlock(node.text ?? '', node.language ?? 'csharp')}\n\n`;
    case 'cref': {
      const type = findTypeByDocId(context.allTypes, node.value ?? '');
      return type
        ? `[${shortTypeName(type.fullName ?? type.name)}](${withBase(context.base, typePath(context.packageName, type))})`
        : inlineCode(cleanDocId(node.value ?? ''));
    }
    case 'href':
    case 'a':
      return `[${markdownText(node.text ?? node.value ?? '')}](${node.value ?? ''})`;
    case 'langword':
    case 'paramref':
    case 'typeparamref':
      return inlineCode(node.value ?? '');
    case 'para':
      return `\n\n${renderDocNodes(node.children, context)}\n\n`;
    case 'note': {
      const label = (node.value ?? 'note').toUpperCase();
      const text = renderDocNodes(node.children, context);
      return `\n\n> [!${label}]\n> ${text.replaceAll('\n', '\n> ')}\n\n`;
    }
    case 'list':
      return renderDocList(node, context);
    default:
      return node.text ?? node.value ?? '';
  }
}

function renderDocList(node: ApiDocNode, context: MarkdownContext): string {
  if (node.style === 'table') {
    const headerTerm = renderDocNodes(node.header?.term, context) || 'Name';
    const headerDescription =
      renderDocNodes(node.header?.description, context) || 'Description';
    const rows = (node.items ?? []).map(
      (item) =>
        `| ${escapeTableCell(renderDocNodes(item.term, context))} | ${escapeTableCell(renderDocNodes(item.description, context))} |`,
    );
    return `\n\n| ${headerTerm} | ${headerDescription} |\n| --- | --- |\n${rows.join('\n')}\n\n`;
  }
  const marker = node.style === 'number' ? '1.' : '-';
  return `\n\n${(node.items ?? [])
    .map((item) => {
      const term = renderDocNodes(item.term, context);
      const description = renderDocNodes(item.description, context);
      return `${marker} ${term ? `**${term}:** ` : ''}${description}`;
    })
    .join('\n')}\n\n`;
}

function renderDocumentation(
  docs: ApiDocumentation | undefined,
  context: MarkdownContext,
): string {
  if (!docs) return '';
  return finalizeMarkdown([
    renderDocNodes(docs.summary, context),
    section('Remarks', renderDocNodes(docs.remarks, context)),
    section('Returns', renderDocNodes(docs.returns, context)),
    docs.typeParameters &&
      section(
        'Type parameters',
        Object.entries(docs.typeParameters)
          .map(([name, value]) => `- \`${name}\`: ${renderDocNodes(value, context)}`)
          .join('\n'),
      ),
    renderExamples(docs, context),
    renderSeeAlso(docs, context),
  ]);
}

function renderMemberSectionMarkdown(
  pkg: PackageApiDocument,
  type: ApiType,
  member: ApiMember,
  context: MarkdownContext,
  includeHeading = true,
): string {
  const source = sourceHref(pkg.package, member.sourceFile, member.sourceLines);
  const parameters = member.parameters?.length
    ? section(
        'Parameters',
        member.parameters
          .map((parameter) => {
            const description = renderDocNodes(
              member.docs?.parameters?.[parameter.name],
              context,
            );
            return `- \`${parameter.name}\` (\`${shortTypeName(parameter.type)}\`)${description ? `: ${description}` : ''}`;
          })
          .join('\n'),
        3,
      )
    : '';
  const exceptions = member.docs?.exceptions?.length
    ? section(
        'Exceptions',
        member.docs.exceptions
          .map(
            (exception) =>
              `- \`${cleanDocId(exception.type)}\`${exception.description ? `: ${renderDocNodes(exception.description, context)}` : ''}`,
          )
          .join('\n'),
        3,
      )
    : '';
  return finalizeMarkdown([
    includeHeading
      ? `## ${markdownText(memberDisplayName(member, type))} {#${memberSlug(member)}}`
      : '',
    includeHeading
      ? `[Dedicated page](${withBase(context.base, memberPath(pkg.package.name, type, member))})`
      : '',
    source ? `[Source](${source})` : '',
    codeBlock(buildMemberSignature(member), 'csharp'),
    renderDocNodes(member.docs?.summary, context),
    parameters,
    section('Returns', renderDocNodes(member.docs?.returns, context), 3),
    section('Value', renderDocNodes(member.docs?.value, context), 3),
    exceptions,
    renderExamples(member.docs, context, 3),
  ]);
}

function renderExamples(
  docs: ApiDocumentation | undefined,
  context: MarkdownContext,
  level = 2,
): string {
  if (!docs?.examples?.length) return '';
  return section(
    'Examples',
    docs.examples
      .map((example) =>
        finalizeMarkdown([
          renderDocNodes(example.description, context),
          codeBlock(example.code, example.language ?? 'csharp'),
        ]),
      )
      .join('\n\n'),
    level,
  );
}

function renderSeeAlso(docs: ApiDocumentation, context: MarkdownContext): string {
  if (!docs.seeAlso?.length) return '';
  return section(
    'See also',
    docs.seeAlso
      .map((docId) => {
        const type = findTypeByDocId(context.allTypes, docId);
        return type
          ? `- [${markdownText(typeDisplayName(type))}](${withBase(context.base, typePath(context.packageName, type))})`
          : `- ${inlineCode(cleanDocId(docId))}`;
      })
      .join('\n'),
  );
}

function inlineSummary(docs: ApiDocumentation | undefined, context: MarkdownContext): string {
  return renderDocNodes(docs?.summary, context).replace(/\s+/g, ' ').trim();
}

function cleanDocId(value: string): string {
  return value.replace(/^[A-Z]:/, '').replace(/``?(\d+)/g, '-$1');
}

function section(title: string, content?: string, level = 2): string {
  return content?.trim() ? `${'#'.repeat(level)} ${title}\n\n${content.trim()}` : '';
}

function codeBlock(code: string, language = ''): string {
  const fence = '`'.repeat(
    Math.max(3, ...[...code.matchAll(/`+/g)].map((match) => match[0].length + 1)),
  );
  return `${fence}${language}\n${code.trim()}\n${fence}`;
}

function inlineCode(value: string): string {
  const fence = value.includes('`') ? '``' : '`';
  return `${fence}${value}${fence}`;
}

function escapeTableCell(value: string): string {
  return value.replaceAll('|', '\\|').replaceAll('\n', '<br>');
}

function finalizeMarkdown(blocks: Array<string | false | null | undefined>): string {
  const value = blocks
    .map((block) => (typeof block === 'string' ? block.trim() : ''))
    .filter(Boolean)
    .join('\n\n')
    .replace(/\n{3,}/g, '\n\n');
  return value ? `${value}\n` : '';
}
