using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;

namespace Orleans.CodeGenerator
{
    public class CodeGeneratorOptions
    {
        public List<string> GenerateSerializerAttributes { get; } = new() { "Orleans.GenerateSerializerAttribute" };
        public List<string> IdAttributes { get; } = new() { "Orleans.IdAttribute" };
        public List<string> AliasAttributes { get; } = new() { "Orleans.AliasAttribute" };
        public List<string> ImmutableAttributes { get; } = new() { "Orleans.ImmutableAttribute" };
        public List<string> ConstructorAttributes { get; } = new() { "Orleans.OrleansConstructorAttribute", "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute" };
        public GenerateFieldIds GenerateFieldIds { get; set; }
        public bool GenerateCompatibilityInvokers { get; set; }
    }

    public class CodeGenerator
    {
        internal const string CodeGeneratorName = "OrleansCodeGen";
        private readonly Dictionary<string, List<MemberDeclarationSyntax>> _namespacedMembers = new();
        private readonly Dictionary<InvokableMethodId, InvokableMethodDescription> _invokableMethodDescriptions = new();
        private readonly HashSet<INamedTypeSymbol> _visitedInterfaces = new(SymbolEqualityComparer.Default);
        private readonly List<string> DisabledWarnings = new() { "CS1591" };

        public CodeGenerator(Compilation compilation, CodeGeneratorOptions options)
        {
            Compilation = compilation;
            Options = options;
            LibraryTypes = LibraryTypes.FromCompilation(compilation, options);
            MetadataModel = new MetadataModel();
            CopierGenerator = new CopierGenerator(this);
            SerializerGenerator = new SerializerGenerator(this);
            ProxyGenerator = new ProxyGenerator(this);
            InvokableGenerator = new InvokableGenerator(this);
            MetadataGenerator = new MetadataGenerator(this);
            ActivatorGenerator = new ActivatorGenerator(this);
        }

        public Compilation Compilation { get; }
        public CodeGeneratorOptions Options { get; }
        internal LibraryTypes LibraryTypes { get; }
        internal MetadataModel MetadataModel { get; }
        internal CopierGenerator CopierGenerator { get; }
        internal SerializerGenerator SerializerGenerator { get; }
        internal ProxyGenerator ProxyGenerator { get; }
        internal InvokableGenerator InvokableGenerator { get; }
        internal MetadataGenerator MetadataGenerator { get; }
        internal ActivatorGenerator ActivatorGenerator { get; }

        public CompilationUnitSyntax GenerateCode(CancellationToken cancellationToken)
        {
            var referencedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var compilationAsm = LibraryTypes.Compilation.Assembly;
            ComputeAssembliesToExamine(compilationAsm, assembliesToExamine);

            // Expand the set of referenced assemblies
            referencedAssemblies.Add(compilationAsm);
            MetadataModel.ApplicationParts.Add(compilationAsm.MetadataName);
            foreach (var reference in LibraryTypes.Compilation.References)
            {
                if (LibraryTypes.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm)
                {
                    continue;
                }

                if (!referencedAssemblies.Add(asm))
                {
                    continue;
                }

                if (asm.GetAttributes(LibraryTypes.ApplicationPartAttribute, out var attrs))
                {
                    MetadataModel.ApplicationParts.Add(asm.MetadataName);
                    foreach (var attr in attrs)
                    {
                        MetadataModel.ApplicationParts.Add((string)attr.ConstructorArguments.First().Value);
                    }
                }
            }

            // The mapping of proxy base types to a mapping of return types to invokable base types. Used to set default invokable base types for each proxy base type.
            var proxyBaseTypeInvokableBaseTypes = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);

            foreach (var asm in assembliesToExamine)
            {
                foreach (var symbol in asm.GetDeclaredTypes())
                {
                    if (GetWellKnownTypeId(symbol) is uint wellKnownTypeId)
                    {
                        MetadataModel.WellKnownTypeIds.Add((symbol.ToOpenTypeSyntax(), wellKnownTypeId));
                    }

                    if (GetAlias(symbol) is string typeAlias)
                    {
                        MetadataModel.TypeAliases.Add((symbol.ToOpenTypeSyntax(), typeAlias));
                    }

                    if (GetCompoundTypeAlias(symbol) is CompoundTypeAliasComponent[] compoundTypeAlias)
                    {
                        MetadataModel.CompoundTypeAliases.Add(compoundTypeAlias, symbol.ToOpenTypeSyntax());
                    }

                    if (FSharpUtilities.IsUnionCase(LibraryTypes, symbol, out var sumType) && ShouldGenerateSerializer(sumType))
                    {
                        if (!Compilation.IsSymbolAccessibleWithin(sumType, Compilation.Assembly))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
                        }

                        var typeDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(Compilation, symbol, LibraryTypes);
                        MetadataModel.SerializableTypes.Add(typeDescription);
                    }
                    else if (ShouldGenerateSerializer(symbol))
                    {
                        if (!Compilation.IsSymbolAccessibleWithin(symbol, Compilation.Assembly))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
                        }

                        if (FSharpUtilities.IsRecord(LibraryTypes, symbol))
                        {
                            var typeDescription = new FSharpUtilities.FSharpRecordTypeDescription(Compilation, symbol, LibraryTypes);
                            MetadataModel.SerializableTypes.Add(typeDescription);
                        }
                        else
                        {
                            // Regular type
                            var includePrimaryConstructorParameters = ShouldIncludePrimaryConstructorParameters(symbol);
                            var constructorParameters = ImmutableArray<IParameterSymbol>.Empty;
                            if (includePrimaryConstructorParameters)
                            {
                                if (symbol.IsRecord)
                                {
                                    // If there is a primary constructor then that will be declared before the copy constructor
                                    // A record always generates a copy constructor and marks it as compiler generated
                                    // todo: find an alternative to this magic
                                    var potentialPrimaryConstructor = symbol.Constructors[0];
                                    if (!potentialPrimaryConstructor.IsImplicitlyDeclared && !potentialPrimaryConstructor.IsCompilerGenerated())
                                    {
                                        constructorParameters = potentialPrimaryConstructor.Parameters;
                                    }
                                }
                                else
                                {
                                    var annotatedConstructors = symbol.Constructors.Where(ctor => ctor.HasAnyAttribute(LibraryTypes.ConstructorAttributeTypes)).ToList();
                                    if (annotatedConstructors.Count == 1)
                                    {
                                        constructorParameters = annotatedConstructors[0].Parameters;
                                    }
                                    else
                                    {
                                        // record structs from referenced assemblies do not return IsRecord=true
                                        // above. See https://github.com/dotnet/roslyn/issues/69326
                                        // So we implement the same heuristics from ShouldIncludePrimaryConstructorParameters
                                        // to detect a primary constructor.
                                        var properties = symbol.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
                                        var primaryConstructor = symbol.GetMembers()
                                            .OfType<IMethodSymbol>()
                                            .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
                                            // Check for a ctor where all parameters have a corresponding compiler-generated prop.
                                            .FirstOrDefault(ctor => ctor.Parameters.All(prm =>
                                                properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));

                                        if (primaryConstructor != null)
                                            constructorParameters = primaryConstructor.Parameters;
                                    }
                                }
                            }

                            var implicitMemberSelectionStrategy = (Options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol)) switch
                            {
                                (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
                                (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
                                _ => GenerateFieldIds.None
                            };
                            var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, LibraryTypes);
                            if (!fieldIdAssignmentHelper.IsValidForSerialization)
                            {
                                throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason));
                            }

                            var typeDescription = new SerializableTypeDescription(Compilation, symbol, includePrimaryConstructorParameters, GetDataMembers(fieldIdAssignmentHelper), LibraryTypes);
                            MetadataModel.SerializableTypes.Add(typeDescription);
                        }
                    }

                    if (symbol.TypeKind == TypeKind.Interface)
                    {
                        VisitInterface(symbol.OriginalDefinition);
                    }

                    if ((symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct)
                        && !symbol.IsAbstract
                        && (symbol.DeclaredAccessibility == Accessibility.Public || symbol.DeclaredAccessibility == Accessibility.Internal))
                    {
                        if (symbol.HasAttribute(LibraryTypes.RegisterSerializerAttribute))
                        {
                            MetadataModel.DetectedSerializers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterActivatorAttribute))
                        {
                            MetadataModel.DetectedActivators.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterCopierAttribute))
                        {
                            MetadataModel.DetectedCopiers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterConverterAttribute))
                        {
                            MetadataModel.DetectedConverters.Add(symbol);
                        }

                        // Find all implementations of invokable interfaces
                        foreach (var iface in symbol.AllInterfaces)
                        {
                            var attribute = iface.GetAttribute(
                                LibraryTypes.GenerateMethodSerializersAttribute,
                                inherited: true);
                            if (attribute != null)
                            {
                                MetadataModel.InvokableInterfaceImplementations.Add(symbol);
                                break;
                            }
                        }
                    }

                    GenerateFieldIds GetGenerateFieldIdsOptionFromType(INamedTypeSymbol t)
                    {
                        var attribute = t.GetAttribute(LibraryTypes.GenerateSerializerAttribute);
                        if (attribute is null)
                            return GenerateFieldIds.None;

                        foreach (var namedArgument in attribute.NamedArguments)
                        {
                            if (namedArgument.Key == "GenerateFieldIds")
                            {
                                var value = namedArgument.Value.Value;
                                return value == null ? GenerateFieldIds.None : (GenerateFieldIds)(int)value;
                            }
                        }
                        return GenerateFieldIds.None;
                    }

                    bool ShouldGenerateSerializer(INamedTypeSymbol t) => t.HasAnyAttribute(LibraryTypes.GenerateSerializerAttributes);

                    bool ShouldIncludePrimaryConstructorParameters(INamedTypeSymbol t)
                    {
                        static bool? TestGenerateSerializerAttribute(INamedTypeSymbol t, INamedTypeSymbol at)
                        {
                            var attribute = t.GetAttribute(at);
                            if (attribute != null)
                            {
                                foreach (var namedArgument in attribute.NamedArguments)
                                {
                                    if (namedArgument.Key == "IncludePrimaryConstructorParameters")
                                    {
                                        if (namedArgument.Value.Kind == TypedConstantKind.Primitive && namedArgument.Value.Value is bool b)
                                        {
                                            return b;
                                        }
                                    }
                                }
                            }

                            // If there is no such named argument, return null so that other attributes have a chance to apply and defaults can be applied.
                            return null;
                        }

                        foreach (var attr in LibraryTypes.GenerateSerializerAttributes)
                        {
                            if (TestGenerateSerializerAttribute(t, attr) is bool res)
                            {
                                return res;
                            }
                        }

                        // Default to true for records.
                        if (t.IsRecord)
                            return true;

                        var properties = t.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();

                        return t.GetMembers()
                            .OfType<IMethodSymbol>()
                            .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
                            // Check for a ctor where all parameters have a corresponding compiler-generated prop.
                            .Any(ctor => ctor.Parameters.All(prm =>
                                properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));
                    }
                }
            }

            // Generate serializers.
            foreach (var type in MetadataModel.SerializableTypes)
            {
                string ns = type.GeneratedNamespace;

                // Generate a partial serializer class for each serializable type.
                var serializer = SerializerGenerator.Generate(type);
                AddMember(ns, serializer);

                // Generate a copier for each serializable type.
                if (CopierGenerator.GenerateCopier(type, MetadataModel.DefaultCopiers) is { } copier)
                    AddMember(ns, copier);

                if (!type.IsAbstractType && !type.IsEnumType && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator && type is not GeneratedInvokableDescription || type.HasActivatorConstructor))
                {
                    MetadataModel.ActivatableTypes.Add(type);

                    // Generate an activator class for types with default constructor or activator constructor.
                    var activator = ActivatorGenerator.GenerateActivator(type);
                    AddMember(ns, activator);
                }
            }

            // Generate metadata.
            var metadataClassNamespace = CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(Compilation.AssemblyName);
            var metadataClass = MetadataGenerator.GenerateMetadata();
            AddMember(ns: metadataClassNamespace, member: metadataClass);
            var metadataAttribute = AttributeList()
                .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)))
                .WithAttributes(
                    SingletonSeparatedList(
                        Attribute(LibraryTypes.TypeManifestProviderAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(TypeOfExpression(QualifiedName(IdentifierName(metadataClassNamespace), IdentifierName(metadataClass.Identifier.Text)))))));

            var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(LibraryTypes, MetadataModel);
            assemblyAttributes.Add(metadataAttribute);

            if (assemblyAttributes.Count > 0)
            {
                assemblyAttributes[0] = assemblyAttributes[0]
                    .WithLeadingTrivia(
                        SyntaxFactory.TriviaList(
                            new List<SyntaxTrivia>
                            {
                                Trivia(
                                   PragmaWarningDirectiveTrivia(
                                       Token(SyntaxKind.DisableKeyword),
                                       SeparatedList(DisabledWarnings.Select(str =>
                                       {
                                           var syntaxToken = SyntaxFactory.Literal(
                                                SyntaxFactory.TriviaList(),
                                                str,
                                                str,
                                                SyntaxFactory.TriviaList());

                                            return (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, syntaxToken);
                                       })),
                                       isActive: true)),
                            }));
            }

            var usings = List(new[] { UsingDirective(ParseName("global::Orleans.Serialization.Codecs")), UsingDirective(ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")) });
            var namespaces = new List<MemberDeclarationSyntax>(_namespacedMembers.Count);
            foreach (var pair in _namespacedMembers)
            {
                var ns = pair.Key;
                var member = pair.Value;

                namespaces.Add(NamespaceDeclaration(ParseName(ns)).WithMembers(List(member)).WithUsings(usings));
            }

            if (namespaces.Count > 0)
            {
                namespaces[0] = namespaces[0]
                    .WithTrailingTrivia(
                       SyntaxFactory.TriviaList(
                           new List<SyntaxTrivia>
                           {
                                Trivia(
                                   PragmaWarningDirectiveTrivia(
                                       Token(SyntaxKind.RestoreKeyword),
                                       SeparatedList(DisabledWarnings.Select(str =>
                                       {
                                           var syntaxToken = SyntaxFactory.Literal(
                                                SyntaxFactory.TriviaList(),
                                                str,
                                                str,
                                                SyntaxFactory.TriviaList());

                                            return (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, syntaxToken);
                                       })),
                                       isActive: true)),
                           }));
            }

            return CompilationUnit()
                .WithAttributeLists(List(assemblyAttributes))
                .WithMembers(List(namespaces));
        }

        public static string GetGeneratedNamespaceName(ITypeSymbol type) => type.GetNamespaceAndNesting() switch
        {
            { Length: > 0 } ns => $"{CodeGeneratorName}.{ns}",
            _ => CodeGeneratorName
        };

        public void AddMember(string ns, MemberDeclarationSyntax member)
        {
            if (!_namespacedMembers.TryGetValue(ns, out var existing))
            {
                existing = _namespacedMembers[ns] = new List<MemberDeclarationSyntax>();
            }

            existing.Add(member);
        }

        private void ComputeAssembliesToExamine(IAssemblySymbol asm, HashSet<IAssemblySymbol> expandedAssemblies)
        {
            if (!expandedAssemblies.Add(asm))
            {
                return;
            }

            if (!asm.GetAttributes(LibraryTypes.GenerateCodeForDeclaringAssemblyAttribute, out var attrs)) return;

            foreach (var attr in attrs)
            {
                var param = attr.ConstructorArguments.First();
                if (param.Kind != TypedConstantKind.Type)
                {
                    throw new ArgumentException($"Unrecognized argument type in attribute [{attr.AttributeClass.Name}({param.ToCSharpString()})]");
                }

                var type = (ITypeSymbol)param.Value;

                // Recurse on the assemblies which the type was declared in.
                var declaringAsm = type.OriginalDefinition.ContainingAssembly;
                if (declaringAsm is null)
                {
                    var diagnostic = GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly_Diagnostic.CreateDiagnostic(attr, type);
                    throw new OrleansGeneratorDiagnosticAnalysisException(diagnostic);
                }
                else
                {
                    ComputeAssembliesToExamine(declaringAsm, expandedAssemblies);
                }
            }
        }

        // Returns descriptions of all data members (fields and properties)
        private IEnumerable<IMemberDescription> GetDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
        {
            var members = new Dictionary<(uint, bool), IMemberDescription>();

            foreach (var member in fieldIdAssignmentHelper.Members)
            {
                if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
                    continue;
                var (id, isConstructorParameter) = key;

                // FieldDescription takes precedence over PropertyDescription (never replace)
                if (member is IPropertySymbol property && !members.TryGetValue((id, isConstructorParameter), out _))
                {
                    members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
                }

                if (member is IFieldSymbol field)
                {
                    // FieldDescription takes precedence over PropertyDescription (add or replace)
                    if (!members.TryGetValue((id, isConstructorParameter), out var existing) || existing is PropertyDescription)
                    {
                        members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                    }
                }
            }
            return members.Values;
        }

        public uint? GetId(ISymbol memberSymbol) => GetId(LibraryTypes, memberSymbol);

        internal static uint? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
        {
            return memberSymbol.GetAnyAttribute(libraryTypes.IdAttributeTypes) is { } attr
                ? (uint)attr.ConstructorArguments.First().Value
                : null;
        }

        internal static string CreateHashedMethodId(IMethodSymbol methodSymbol)
        {
            var methodSignature = Format(methodSymbol);
            var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(methodSignature));
            return $"{HexConverter.ToString(hash)}";

            static string Format(IMethodSymbol methodInfo)
            {
                var result = new StringBuilder();
                result.Append(methodInfo.ContainingType.ToDisplayName());
                result.Append('.');
                result.Append(methodInfo.Name);

                if (methodInfo.IsGenericMethod)
                {
                    result.Append('<');
                    var first = true;
                    foreach (var typeArgument in methodInfo.TypeArguments)
                    {
                        if (!first) result.Append(',');
                        else first = false;
                        result.Append(typeArgument.Name);
                    }

                    result.Append('>');
                }

                {
                    result.Append('(');
                    var parameters = methodInfo.Parameters;
                    var first = true;
                    foreach (var parameter in parameters)
                    {
                        if (!first)
                        {
                            result.Append(',');
                        }

                        var parameterType = parameter.Type;
                        switch (parameterType)
                        {
                            case ITypeParameterSymbol _:
                                result.Append(parameterType.Name);
                                break;
                            default:
                                result.Append(parameterType.ToDisplayName());
                                break;
                        }

                        first = false;
                    }
                }

                result.Append(')');
                return result.ToString();
            }
        }

        private uint? GetWellKnownTypeId(ISymbol symbol) => GetId(symbol);

        public string GetAlias(ISymbol symbol) => (string)symbol.GetAttribute(LibraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;

        private CompoundTypeAliasComponent[] GetCompoundTypeAlias(ISymbol symbol)
        {
            var attr = symbol.GetAttribute(LibraryTypes.CompoundTypeAliasAttribute);
            if (attr is null)
            {
                return null;
            }

            var allArgs = attr.ConstructorArguments;
            if (allArgs.Length != 1 || allArgs[0].Values.Length == 0)
            {
                throw new ArgumentException($"Unsupported arguments in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]");
            }

            var args = allArgs[0].Values;
            var result = new CompoundTypeAliasComponent[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.IsNull)
                {
                    throw new ArgumentNullException($"Unsupported null argument in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]");
                }

                result[i] = arg.Value switch
                {
                    ITypeSymbol type => new CompoundTypeAliasComponent(type),
                    string str => new CompoundTypeAliasComponent(str),
                    _ => throw new ArgumentException($"Unrecognized argument type for argument {arg.ToCSharpString()} in attribute [{attr.AttributeClass.Name}({string.Join(", ", allArgs.Select(a => a.ToCSharpString()))})]"),
                };
            }

            return result;
        }

        internal static AttributeListSyntax GetGeneratedCodeAttributes() => GeneratedCodeAttributeSyntax;
        private static readonly AttributeListSyntax GeneratedCodeAttributeSyntax =
            AttributeList().AddAttributes(
                Attribute(ParseName("global::System.CodeDom.Compiler.GeneratedCodeAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
                        AttributeArgument(typeof(CodeGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression())),
                Attribute(ParseName("global::System.ComponentModel.EditorBrowsableAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(ParseName("global::System.ComponentModel.EditorBrowsableState").Member("Never")))
            );

        internal static AttributeSyntax GetMethodImplAttributeSyntax() => MethodImplAttributeSyntax;
        private static readonly AttributeSyntax MethodImplAttributeSyntax =
            Attribute(ParseName("global::System.Runtime.CompilerServices.MethodImplAttribute"))
                .AddArgumentListArguments(AttributeArgument(ParseName("global::System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));

        internal void VisitInterface(INamedTypeSymbol interfaceType)
        {
            // Get or generate an invokable for the original method definition.
            if (!SymbolEqualityComparer.Default.Equals(interfaceType, interfaceType.OriginalDefinition))
            {
                interfaceType = interfaceType.OriginalDefinition;
            }

            if (!_visitedInterfaces.Add(interfaceType))
            {
                return;
            }

            foreach (var proxyBase in GetProxyBases(interfaceType))
            {
                _ = GetInvokableInterfaceDescription(proxyBase.ProxyBaseType, interfaceType);
            }

            /*
            foreach (var baseInterface in interfaceType.AllInterfaces)
            {
                VisitInterface(baseInterface);
            }
            */
        }

        internal bool TryGetInvokableInterfaceDescription(INamedTypeSymbol interfaceType, out ProxyInterfaceDescription result)
        {
            if (!TryGetProxyBaseDescription(interfaceType, out var description))
            {
                result = null;
                return false;
            }

            result = GetInvokableInterfaceDescription(description.ProxyBaseType, interfaceType);
            return true;
        }

        private readonly Dictionary<INamedTypeSymbol, List<InvokableMethodProxyBase>> _interfaceProxyBases = new(SymbolEqualityComparer.Default);
        internal List<InvokableMethodProxyBase> GetProxyBases(INamedTypeSymbol interfaceType)
        {
            if (_interfaceProxyBases.TryGetValue(interfaceType, out var result))
            {
                return result;
            }

            result = new List<InvokableMethodProxyBase>();
            if (interfaceType.GetAttributes(LibraryTypes.GenerateMethodSerializersAttribute, out var attributes, inherited: true))
            {
                foreach (var attribute in attributes)
                {
                    var proxyBase = GetProxyBaseDescription(attribute);
                    if (!result.Contains(proxyBase))
                    {
                        result.Add(proxyBase);
                    }
                }
            }

            return result;
        }

        internal bool TryGetProxyBaseDescription(INamedTypeSymbol interfaceType, out InvokableMethodProxyBase result)
        {
            var attribute = interfaceType.GetAttribute(LibraryTypes.GenerateMethodSerializersAttribute, inherited: true);
            if (attribute == null)
            {
                result = null;
                return false;
            }

            result = GetProxyBaseDescription(attribute);
            return true;
        }

        private InvokableMethodProxyBase GetProxyBaseDescription(AttributeData attribute)
        {
            var proxyBaseType = ((INamedTypeSymbol)attribute.ConstructorArguments[0].Value).OriginalDefinition;
            var isExtension = (bool)attribute.ConstructorArguments[1].Value;
            var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseType);
            var descriptor = new InvokableMethodProxyBaseId(proxyBaseType, isExtension);
            var description = new InvokableMethodProxyBase(this, descriptor, invokableBaseTypes);
            return description;

            Dictionary<INamedTypeSymbol, INamedTypeSymbol> GetInvokableBaseTypes(INamedTypeSymbol baseClass)
            {
                // Set the base invokable types which are used if attributes on individual methods do not override them.
                if (!MetadataModel.ProxyBaseTypeInvokableBaseTypes.TryGetValue(baseClass, out var invokableBaseTypes))
                {
                    invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    if (baseClass.GetAttributes(LibraryTypes.DefaultInvokableBaseTypeAttribute, out var invokableBaseTypeAttributes))
                    {
                        foreach (var attr in invokableBaseTypeAttributes)
                        {
                            var ctorArgs = attr.ConstructorArguments;
                            var returnType = (INamedTypeSymbol)ctorArgs[0].Value;
                            var invokableBaseType = (INamedTypeSymbol)ctorArgs[1].Value;
                            invokableBaseTypes[returnType] = invokableBaseType;
                        }
                    }

                    MetadataModel.ProxyBaseTypeInvokableBaseTypes[baseClass] = invokableBaseTypes;
                }

                return invokableBaseTypes;
            }
        }

        internal InvokableMethodProxyBase GetProxyBase(INamedTypeSymbol interfaceType)
        {
            if (!TryGetProxyBaseDescription(interfaceType, out var result))
            {
                throw new InvalidOperationException($"Cannot get proxy base description for a type which does not have or inherit [{nameof(LibraryTypes.GenerateMethodSerializersAttribute)}]");
            }

            return result;
        }

        private ProxyInterfaceDescription GetInvokableInterfaceDescription(INamedTypeSymbol proxyBaseType, INamedTypeSymbol interfaceType)
        {
            var originalInterface = interfaceType.OriginalDefinition;
            if (MetadataModel.InvokableInterfaces.TryGetValue(originalInterface, out var description))
            {
                return description;
            }

            description = new ProxyInterfaceDescription(this, proxyBaseType, originalInterface);
            MetadataModel.InvokableInterfaces.Add(originalInterface, description);

            // Generate a proxy.
            var (generatedClass, proxyDescription) = ProxyGenerator.Generate(description);

            // Emit the generated proxy
            if (Compilation.GetTypeByMetadataName(proxyDescription.MetadataName) == null)
            {
                AddMember(proxyDescription.InterfaceDescription.GeneratedNamespace, generatedClass);
            }

            MetadataModel.GeneratedProxies.Add(proxyDescription);

            return description;
        }

        internal ProxyMethodDescription GetProxyMethodDescription(INamedTypeSymbol interfaceType, IMethodSymbol method)
        {
            var originalMethod = method.OriginalDefinition;
            var proxyBaseInfo = GetProxyBase(interfaceType);

            // For extensions, we want to ensure that the containing type is always the extension.
            // This ensures that we will always know which 'component' to get in our SetTarget method.
            // If the type is not an extension, use the original method definition's containing type.
            // This is the interface where the type was originally defined.
            var containingType = proxyBaseInfo.IsExtension ? interfaceType : originalMethod.ContainingType;

            var invokableId = new InvokableMethodId(proxyBaseInfo, containingType, originalMethod);
            var interfaceDescription = GetInvokableInterfaceDescription(invokableId.ProxyBase.ProxyBaseType, interfaceType);

            // Get or generate an invokable for the original method definition.
            if (!MetadataModel.GeneratedInvokables.TryGetValue(invokableId, out var generatedInvokable))
            {
                if (!_invokableMethodDescriptions.TryGetValue(invokableId, out var methodDescription))
                {
                    methodDescription = _invokableMethodDescriptions[invokableId] = InvokableMethodDescription.Create(invokableId, containingType);
                }

                generatedInvokable = MetadataModel.GeneratedInvokables[invokableId] = InvokableGenerator.Generate(methodDescription);

                if (Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) == null)
                {
                    // Emit the generated code on-demand.
                    AddMember(generatedInvokable.GeneratedNamespace, generatedInvokable.ClassDeclarationSyntax);

                    // Ensure the type will have a serializer generated for it.
                    MetadataModel.SerializableTypes.Add(generatedInvokable);

                    foreach (var alias in generatedInvokable.CompoundTypeAliases)
                    {
                        MetadataModel.CompoundTypeAliases.Add(alias, generatedInvokable.OpenTypeSyntax);
                    }
                }
            }

            var proxyMethodDescription = ProxyMethodDescription.Create(interfaceDescription, generatedInvokable, method);

            // For backwards compatibility, generate invokers for the specific implementation types as well, where they differ.
            if (Options.GenerateCompatibilityInvokers && !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType, interfaceType))
            {
                var compatInvokableId = new InvokableMethodId(proxyBaseInfo, interfaceType, method);
                var compatMethodDescription = InvokableMethodDescription.Create(compatInvokableId, interfaceType);
                var compatInvokable = InvokableGenerator.Generate(compatMethodDescription);
                AddMember(compatInvokable.GeneratedNamespace, compatInvokable.ClassDeclarationSyntax);
                var alias =
                    InvokableGenerator.GetCompoundTypeAliasComponents(
                        compatInvokableId,
                        interfaceType,
                        compatMethodDescription.GeneratedMethodId);
                MetadataModel.CompoundTypeAliases.Add(alias, compatInvokable.OpenTypeSyntax);
            }

            return proxyMethodDescription;
        }
    }
}
