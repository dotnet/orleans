export type MemberKind =
  | 'constructor'
  | 'property'
  | 'method'
  | 'field'
  | 'event'
  | 'indexer';

export interface ApiGenericParameter {
  name: string;
  constraints?: string[];
}

export interface ApiAttribute {
  name: string;
  constructorArguments?: string[];
  arguments?: Record<string, string>;
}

export interface ApiParameter {
  name: string;
  type: string;
  isNullable?: boolean;
  isOptional?: boolean;
  defaultValue?: string;
  modifier?: string;
  attributes?: ApiAttribute[];
}

export interface ApiDocListItem {
  term?: ApiDocNode[];
  description: ApiDocNode[];
}

export interface ApiDocNode {
  kind: string;
  text?: string;
  value?: string;
  language?: string;
  children?: ApiDocNode[];
  style?: string;
  header?: ApiDocListItem;
  items?: ApiDocListItem[];
}

export interface ApiDocException {
  type: string;
  description?: ApiDocNode[];
}

export interface ApiDocExample {
  code: string;
  language?: string;
  description?: ApiDocNode[];
  region?: string;
}

export interface ApiDocumentation {
  summary?: ApiDocNode[] | string;
  remarks?: ApiDocNode[] | string;
  returns?: ApiDocNode[] | string;
  parameters?: Record<string, ApiDocNode[]>;
  typeParameters?: Record<string, ApiDocNode[]>;
  exceptions?: ApiDocException[];
  examples?: ApiDocExample[];
  value?: ApiDocNode[] | string;
  seeAlso?: string[];
}

export interface ApiEnumMember {
  name: string;
  value: number | string;
  description?: string;
}

export interface ApiMember {
  name: string;
  kind: MemberKind;
  accessibility?: string;
  signature: string;
  returnType?: string;
  isReturnNullable?: boolean;
  isStatic?: boolean;
  isAbstract?: boolean;
  isVirtual?: boolean;
  isOverride?: boolean;
  isAsync?: boolean;
  isExtension?: boolean;
  hasGet?: boolean;
  hasSet?: boolean;
  isInitOnly?: boolean;
  isConst?: boolean;
  isReadOnly?: boolean;
  parameters?: ApiParameter[];
  genericParameters?: ApiGenericParameter[];
  attributes?: ApiAttribute[];
  docs?: ApiDocumentation;
  sourceFile?: string;
  sourceLines?: string;
}

export interface ApiType {
  name: string;
  fullName?: string;
  namespace?: string;
  kind: string;
  accessibility?: string;
  isAbstract?: boolean;
  isSealed?: boolean;
  isStatic?: boolean;
  isGeneric?: boolean;
  isReadOnly?: boolean;
  genericParameters?: ApiGenericParameter[];
  baseType?: string;
  interfaces?: string[];
  nestedTypes?: string[];
  delegateReturnType?: string;
  delegateParameters?: ApiParameter[];
  docs?: ApiDocumentation;
  enumMembers?: ApiEnumMember[];
  attributes?: ApiAttribute[];
  sourceFile?: string;
  sourceLines?: string;
  members?: ApiMember[];
}

export interface ApiPackageMetadata {
  name: string;
  version: string;
  targetFramework: string;
  sourceRepository?: string;
  sourceCommit?: string;
}

export interface PackageApiDocument {
  $schema?: string;
  schemaVersion?: string;
  package: ApiPackageMetadata;
  apiHash?: string;
  types: ApiType[];
}
