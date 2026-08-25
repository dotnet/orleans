import type {
  ApiGenericParameter,
  ApiMember,
  ApiPackageMetadata,
  ApiParameter,
  ApiType,
  MemberKind,
  PackageApiDocument,
} from './types';
import unpublishedApiPackages from '../../data/unpublished-api-packages.json';

export const apiRoot = '/docs/api/csharp';
export const memberKindOrder: MemberKind[] = [
  'constructor',
  'property',
  'method',
  'field',
  'event',
  'indexer',
];
export const memberKindLabels: Record<MemberKind, string> = {
  constructor: 'Constructors',
  property: 'Properties',
  method: 'Methods',
  field: 'Fields',
  event: 'Events',
  indexer: 'Indexers',
};
export const memberKindSlugs: Record<MemberKind, string> = {
  constructor: 'constructors',
  property: 'properties',
  method: 'methods',
  field: 'fields',
  event: 'events',
  indexer: 'indexers',
};

export function genericArity(type: { genericParameters?: ApiGenericParameter[] }): number {
  return type.genericParameters?.length ?? 0;
}

export function typeDisplayName(type: Pick<ApiType, 'name' | 'isGeneric' | 'genericParameters'>) {
  return type.isGeneric && type.genericParameters?.length
    ? `${type.name}<${type.genericParameters.map((parameter) => parameter.name).join(', ')}>`
    : type.name;
}

export function slugify(name: string, arity = 0): string {
  const slug = name
    .toLowerCase()
    .replace(/[^a-z0-9.]+/g, '-')
    .replace(/^-|-$/g, '');
  return arity > 0 ? `${slug}-${arity}` : slug;
}

export function typeSlug(type: ApiType): string {
  let identity =
    type.fullName ?? (type.namespace ? `${type.namespace}.${type.name}` : type.name);
  while (/<[^<>]*>/.test(identity)) {
    identity = identity.replace(/<[^<>]*>/g, '');
  }
  return slugify(identity, genericArity(type));
}

export function packageSlug(name: string): string {
  return name.toLowerCase();
}

export function packagePath(packageName: string): string {
  return `${apiRoot}/${packageSlug(packageName)}/`;
}

export function typePath(packageName: string, type: ApiType): string {
  return `${packagePath(packageName)}${typeSlug(type)}/`;
}

export function memberKindPath(packageName: string, type: ApiType, kind: MemberKind): string {
  return `${typePath(packageName, type)}${memberKindSlugs[kind]}/`;
}

export function memberPath(
  packageName: string,
  type: ApiType,
  member: ApiMember,
): string {
  return `${memberKindPath(packageName, type, member.kind)}${memberSlug(member)}/`;
}

export function withBase(base: string, route: string): string {
  const normalizedBase = base === '/' ? '' : base.replace(/\/$/, '');
  return `${normalizedBase}${route}`;
}

export function markdownCompanionPath(route: string): string {
  return `${route.replace(/\/$/, '')}.md`;
}

export function nugetHref(
  packageName: string,
  unpublishedPackages: Readonly<Record<string, string>> = unpublishedApiPackages.packages,
): string | undefined {
  if (Object.hasOwn(unpublishedPackages, packageName)) {
    return undefined;
  }
  return `https://www.nuget.org/packages/${encodeURIComponent(packageName)}`;
}

export function sourceHref(
  metadata: ApiPackageMetadata,
  sourceFile?: string,
  sourceLines?: string,
): string | undefined {
  const revision = process.env.ORLEANS_DOCS_SOURCE_COMMIT ?? metadata.sourceCommit;
  if (!metadata.sourceRepository || !revision || !sourceFile) {
    return undefined;
  }
  return `${metadata.sourceRepository.replace(/\/$/, '')}/blob/${revision}/${sourceFile}${lineFragment(sourceLines)}`;
}

function lineFragment(sourceLines?: string): string {
  const match = /^(\d+)(?:-(\d+))?$/.exec(sourceLines ?? '');
  if (!match) {
    return '';
  }
  return match[2] && match[2] !== match[1] ? `#L${match[1]}-L${match[2]}` : `#L${match[1]}`;
}

export function groupTypesByNamespace(types: ApiType[]): Map<string, ApiType[]> {
  const groups = new Map<string, ApiType[]>();
  for (const type of types) {
    const namespace = type.namespace || '(global)';
    const values = groups.get(namespace) ?? [];
    values.push(type);
    groups.set(namespace, values);
  }
  return new Map(
    [...groups.entries()]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([namespace, values]) => [
        namespace,
        values.sort((left, right) => left.name.localeCompare(right.name)),
      ]),
  );
}

export function groupMembersByKind(members: ApiMember[] = []): Map<MemberKind, ApiMember[]> {
  const groups = new Map<MemberKind, ApiMember[]>();
  for (const kind of memberKindOrder) {
    const values = members.filter((member) => member.kind === kind);
    if (values.length > 0) {
      groups.set(kind, values);
    }
  }
  return groups;
}

export function shortTypeName(fullName: string): string {
  const firstAngle = fullName.indexOf('<');
  if (firstAngle < 0) {
    return fullName.split('.').at(-1) ?? fullName;
  }
  const outer = fullName.slice(0, firstAngle).split('.').at(-1) ?? fullName;
  const argumentsText = fullName.slice(firstAngle + 1, fullName.lastIndexOf('>'));
  const argumentsList: string[] = [];
  let current = '';
  let depth = 0;
  for (const character of argumentsText) {
    if (character === '<') depth += 1;
    if (character === '>') depth -= 1;
    if (character === ',' && depth === 0) {
      argumentsList.push(current.trim());
      current = '';
    } else {
      current += character;
    }
  }
  if (current.trim()) {
    argumentsList.push(current.trim());
  }
  return `${outer}<${argumentsList.map(shortTypeName).join(', ')}>`;
}

export function memberSlug(member: ApiMember): string {
  let name = member.name === '.ctor' ? 'constructor' : member.name;
  const genericArity = member.genericParameters?.length ?? 0;
  if (genericArity > 0) {
    name = `${name}-${genericArity}`;
  }
  if (member.kind === 'indexer' && member.parameters?.length) {
    name = `this[${member.parameters.map(memberParameterSlug).join(', ')}]`;
  } else if (
    (member.kind === 'method' || member.kind === 'constructor') &&
    member.parameters
  ) {
    name = `${name}(${member.parameters.map(memberParameterSlug).join(', ')})`;
  }
  const readable = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
    .slice(0, 80)
    .replace(/-+$/g, '');
  return `${readable}-${stableRouteHash(member.signature)}`;
}

function memberParameterSlug(parameter: ApiParameter): string {
  const type = parameter.type
    .replace(/\[([,]*)\]/g, (_match, commas: string) => ` array-${commas.length + 1} `)
    .replace(/\?/g, ' nullable ')
    .replace(/\*/g, ' pointer ')
    .replace(/&/g, ' byref ');
  return `${parameter.modifier ? `${parameter.modifier} ` : ''}${type}`;
}

function stableRouteHash(value: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

export function memberDisplayName(member: ApiMember, declaringType?: ApiType): string {
  const name = member.name === '.ctor' && declaringType
    ? typeDisplayName(declaringType)
    : operatorDisplayName(member) ?? member.name;
  if (member.kind === 'indexer' && member.parameters?.length) {
    return `this[${member.parameters.map((parameter) => shortTypeName(parameter.type)).join(', ')}]`;
  }
  if (
    (member.kind === 'method' || member.kind === 'constructor') &&
    member.parameters
  ) {
    return `${name}(${member.parameters.map((parameter) => shortTypeName(parameter.type)).join(', ')})`;
  }
  return name;
}

function operatorDisplayName(member: ApiMember): string | undefined {
  if (!member.name.startsWith('op_')) {
    return undefined;
  }
  return /\.((?:(?:implicit|explicit)\s+)?operator\s+[^(]+)\(/.exec(member.signature)?.[1];
}

export function buildTypeSignature(type: ApiType): string {
  const attributes = formatAttributes(type.attributes);
  if (type.kind === 'delegate') {
    const modifiers = [type.accessibility, 'delegate'].filter(Boolean).join(' ');
    const parameters = (type.delegateParameters ?? [])
      .map((parameter) => `${parameter.modifier ? `${parameter.modifier} ` : ''}${shortTypeName(parameter.type)} ${parameter.name}`)
      .join(', ');
    const declaration = `${modifiers} ${shortTypeName(type.delegateReturnType ?? 'void')} ${typeDisplayName(type)}(${parameters});`;
    return attributes ? `${attributes}\n${declaration}` : declaration;
  }

  const modifiers = [
    type.accessibility,
    type.isStatic ? 'static' : undefined,
    type.isAbstract && type.kind === 'class' ? 'abstract' : undefined,
    type.isSealed && type.kind === 'class' ? 'sealed' : undefined,
    type.isReadOnly ? 'readonly' : undefined,
    type.kind,
  ].filter(Boolean);
  let signature = `${modifiers.join(' ')} ${typeDisplayName(type)}`;
  const inheritance = [type.baseType, ...(type.interfaces ?? [])].filter(
    (value): value is string => Boolean(value),
  );
  if (inheritance.length > 0) {
    signature += ` : ${inheritance.map((value) => shortTypeName(value)).join(', ')}`;
  }
  for (const parameter of type.genericParameters ?? []) {
    if (parameter.constraints?.length) {
      signature += `\n    where ${parameter.name} : ${parameter.constraints.join(', ')}`;
    }
  }
  return attributes ? `${attributes}\n${signature}` : signature;
}

export function cleanMemberSignature(signature: string): string {
  const openingParenthesis = signature.indexOf('(');
  const prefixEnd = openingParenthesis < 0 ? signature.length : openingParenthesis;
  const prefix = signature.slice(0, prefixEnd);
  let depth = 0;
  let lastDot = -1;
  for (let index = 0; index < prefix.length; index += 1) {
    if (prefix[index] === '<') depth += 1;
    else if (prefix[index] === '>') depth -= 1;
    else if (prefix[index] === '.' && depth === 0) lastDot = index;
  }

  if (lastDot < 0) {
    return signature;
  }
  const typeStart = prefix.lastIndexOf(' ', lastDot - 1) + 1;
  return typeStart < lastDot
    ? signature.slice(0, typeStart) + signature.slice(lastDot + 1)
    : signature;
}

export function buildMemberSignature(member: ApiMember): string {
  const attributes = formatAttributes(member.attributes);
  const signature = cleanMemberSignature(member.signature);
  return attributes ? `${attributes}\n${signature}` : signature;
}

function formatAttributes(attributes?: ApiMember['attributes']): string {
  return (attributes ?? [])
    .map((attribute) => {
      const name = attribute.name.replace(/Attribute$/, '');
      const argumentsList = [
        ...(attribute.constructorArguments ?? []),
        ...Object.entries(attribute.arguments ?? {}).map(
          ([key, value]) => `${key} = ${value}`,
        ),
      ];
      return `[${name}${argumentsList.length ? `(${argumentsList.join(', ')})` : ''}]`;
    })
    .join('\n');
}

export function findTypeByDocId(types: ApiType[], docId: string): ApiType | undefined {
  const colon = docId.indexOf(':');
  const fullName = colon >= 0 ? docId.slice(colon + 1) : docId;
  const nameWithoutParameters = fullName.replace(/\(.*$/, '');
  return types.find((type) => {
    if (!type.fullName) {
      return false;
    }

    const arity = genericArity(type);
    const baseName = type.fullName.replace(/`\d+$/, '');
    const encodedName = `${baseName}${arity > 0 ? `\`${arity}` : ''}`;
    return (
      nameWithoutParameters === encodedName ||
      nameWithoutParameters.startsWith(`${encodedName}.`)
    );
  });
}

export function findTypeTargetByDocId(
  packages: PackageApiDocument[],
  docId: string,
  currentPackageName?: string,
): { packageName: string; type: ApiType } | undefined {
  const ordered = currentPackageName
    ? [
        ...packages.filter((pkg) => pkg.package.name === currentPackageName),
        ...packages.filter((pkg) => pkg.package.name !== currentPackageName),
      ]
    : packages;
  for (const pkg of ordered) {
    const type = findTypeByDocId(pkg.types, docId);
    if (type) {
      return { packageName: pkg.package.name, type };
    }
  }
  return undefined;
}

export function assertUniqueApiRoutes(packages: PackageApiDocument[]): void {
  const packageSlugs = new Set<string>();
  for (const pkg of packages) {
    const pkgSlug = packageSlug(pkg.package.name);
    if (packageSlugs.has(pkgSlug)) {
      throw new Error(`Duplicate API package route '${pkgSlug}'.`);
    }
    packageSlugs.add(pkgSlug);
    const typeSlugs = new Set<string>();
    for (const type of pkg.types) {
      if (!type.name) {
        continue;
      }
      const routeSlug = typeSlug(type);
      if (typeSlugs.has(routeSlug)) {
        throw new Error(
          `Duplicate API type route '${pkgSlug}/${routeSlug}' in package '${pkg.package.name}'.`,
        );
      }
      typeSlugs.add(routeSlug);
      const memberSlugs = new Set<string>();
      for (const member of type.members ?? []) {
        const route = `${memberKindSlugs[member.kind]}/${memberSlug(member)}`;
        if (memberSlugs.has(route)) {
          throw new Error(
            `Duplicate API member route '${pkgSlug}/${routeSlug}/${route}' in package '${pkg.package.name}'.`,
          );
        }
        memberSlugs.add(route);
      }
    }
  }
}
