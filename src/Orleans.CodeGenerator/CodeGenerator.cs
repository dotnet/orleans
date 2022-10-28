using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System.Collections.Immutable;
using Orleans.CodeGenerator.Hashing;
using System.Text;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;
using Orleans.CodeGenerator.Diagnostics;

namespace Orleans.CodeGenerator
{
    public class CodeGeneratorOptions
    {
        public List<string> GenerateSerializerAttributes { get; } = new() { "Orleans.GenerateSerializer" };
        public List<string> IdAttributes { get; } = new() { "Orleans.IdAttribute" };
        public List<string> AliasAttributes { get; } = new() { "Orleans.AliasAttribute" };
        public List<string> ImmutableAttributes { get; } = new() { "Orleans.ImmutableAttribute" };
        public List<string> ConstructorAttributes { get; } = new() { "Orleans.OrleansConstructorAttribute", "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute" };
        public GenerateFieldIds GenerateFieldIds { get; set; }
    }

    public class CodeGenerator
    {
        internal const string CodeGeneratorName = "OrleansCodeGen";
        private readonly Compilation _compilation;
        private readonly CodeGeneratorOptions _options;
        private readonly INamedTypeSymbol[] _generateSerializerAttributes;

        public CodeGenerator(Compilation compilation, CodeGeneratorOptions options)
        {
            _compilation = compilation;
            _options = options;
            LibraryTypes = LibraryTypes.FromCompilation(compilation, options);
            _generateSerializerAttributes = options.GenerateSerializerAttributes.Select(compilation.GetTypeByMetadataName).ToArray();
        }

        internal LibraryTypes LibraryTypes { get; }

        public CompilationUnitSyntax GenerateCode(CancellationToken cancellationToken)
        {
            // Collect metadata from the compilation.
            var metadataModel = GenerateMetadataModel(cancellationToken);
            var nsMembers = new Dictionary<string, List<MemberDeclarationSyntax>>();

            foreach (var type in metadataModel.InvokableInterfaces)
            {
                string ns = type.GeneratedNamespace;
                foreach (var method in type.Methods)
                {
                    var (invokable, generatedInvokerDescription) = InvokableGenerator.Generate(LibraryTypes, type, method);
                    metadataModel.SerializableTypes.Add(generatedInvokerDescription);
                    metadataModel.GeneratedInvokables[method] = generatedInvokerDescription;
                    if (generatedInvokerDescription.CompoundTypeAliasArguments is { Length: > 0 } compoundTypeAliasArguments)
                    {
                        metadataModel.CompoundTypeAliases.Add(compoundTypeAliasArguments, generatedInvokerDescription.OpenTypeSyntax);
                    }

                    AddMember(ns, invokable);
                }

                var (proxy, generatedProxyDescription) = ProxyGenerator.Generate(LibraryTypes, type, metadataModel);
                metadataModel.GeneratedProxies.Add(generatedProxyDescription);
                AddMember(ns, proxy);
            }

            // Generate code.
            foreach (var type in metadataModel.SerializableTypes)
            {
                string ns = type.GeneratedNamespace;

                // Generate a partial serializer class for each serializable type.
                var serializer = SerializerGenerator.GenerateSerializer(LibraryTypes, type);
                AddMember(ns, serializer);

                // Generate a copier for each serializable type.
                if (CopierGenerator.GenerateCopier(LibraryTypes, type, metadataModel.DefaultCopiers) is { } copier)
                    AddMember(ns, copier);

                if (!type.IsEnumType && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator && type is not GeneratedInvokerDescription || type.HasActivatorConstructor))
                {
                    metadataModel.ActivatableTypes.Add(type);

                    // Generate an activator class for types with default constructor or activator constructor.
                    var activator = ActivatorGenerator.GenerateActivator(LibraryTypes, type);
                    AddMember(ns, activator);
                }
            }

            // Generate metadata.
            var metadataClassNamespace = CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_compilation.AssemblyName);
            var metadataClass = MetadataGenerator.GenerateMetadata(_compilation, metadataModel, LibraryTypes);
            AddMember(ns: metadataClassNamespace, member: metadataClass);
            var metadataAttribute = AttributeList()
                .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)))
                .WithAttributes(
                    SingletonSeparatedList(
                        Attribute(LibraryTypes.TypeManifestProviderAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(TypeOfExpression(QualifiedName(IdentifierName(metadataClassNamespace), IdentifierName(metadataClass.Identifier.Text)))))));

            var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(LibraryTypes, metadataModel);
            assemblyAttributes.Add(metadataAttribute);

            var usings = List(new[] { UsingDirective(ParseName("global::Orleans.Serialization.Codecs")), UsingDirective(ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")) });
            var namespaces = new List<MemberDeclarationSyntax>(nsMembers.Count);
            foreach (var pair in nsMembers)
            {
                var ns = pair.Key;
                var member = pair.Value;

                namespaces.Add(NamespaceDeclaration(ParseName(ns)).WithMembers(List(member)).WithUsings(usings));
            }

            return CompilationUnit()
                .WithAttributeLists(List(assemblyAttributes))
                .WithMembers(List(namespaces));

            void AddMember(string ns, MemberDeclarationSyntax member)
            {
                if (!nsMembers.TryGetValue(ns, out var existing))
                {
                    existing = nsMembers[ns] = new List<MemberDeclarationSyntax>();
                }

                existing.Add(member);
            }
        }

        private MetadataModel GenerateMetadataModel(CancellationToken cancellationToken)
        {
            var metadataModel = new MetadataModel();
            var referencedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var compilationAsm = LibraryTypes.Compilation.Assembly;
            ComputeAssembliesToExamine(compilationAsm, assembliesToExamine);

            // Expand the set of referenced assemblies
            referencedAssemblies.Add(compilationAsm);
            metadataModel.ApplicationParts.Add(compilationAsm.MetadataName);
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
                    metadataModel.ApplicationParts.Add(asm.MetadataName);
                    foreach (var attr in attrs)
                    {
                        metadataModel.ApplicationParts.Add((string)attr.ConstructorArguments.First().Value);
                    }
                }
            }

            // The mapping of proxy base types to a mapping of return types to invokable base types. Used to set default invokable base types for each proxy base type.
            var proxyBaseTypeInvokableBaseTypes = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);

            foreach (var asm in assembliesToExamine)
            {
                foreach (var symbol in asm.GetDeclaredTypes())
                {
                    var syntaxTree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ?? _compilation.SyntaxTrees.First();
                    var semanticModel = _compilation.GetSemanticModel(syntaxTree);

                    if (GetWellKnownTypeId(symbol) is uint wellKnownTypeId)
                    {
                        metadataModel.WellKnownTypeIds.Add((symbol.ToOpenTypeSyntax(), wellKnownTypeId));
                    }

                    if (GetAlias(symbol) is string typeAlias)
                    {
                        metadataModel.TypeAliases.Add((symbol.ToOpenTypeSyntax(), typeAlias));
                    }

                    if (GetCompoundTypeAlias(symbol) is CompoundTypeAliasComponent[] compoundTypeAlias)
                    {
                        metadataModel.CompoundTypeAliases.Add(compoundTypeAlias, symbol.ToOpenTypeSyntax());
                    }

                    if (FSharpUtilities.IsUnionCase(LibraryTypes, symbol, out var sumType) && ShouldGenerateSerializer(sumType))
                    {
                        if (!semanticModel.IsAccessible(0, sumType))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
                        }

                        var typeDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(semanticModel, symbol, LibraryTypes);
                        metadataModel.SerializableTypes.Add(typeDescription);
                    }
                    else if (ShouldGenerateSerializer(symbol))
                    {
                        if (!semanticModel.IsAccessible(0, symbol))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
                        }

                        if (FSharpUtilities.IsRecord(LibraryTypes, symbol))
                        {
                            var typeDescription = new FSharpUtilities.FSharpRecordTypeDescription(semanticModel, symbol, LibraryTypes);
                            metadataModel.SerializableTypes.Add(typeDescription);
                        }
                        else
                        {
                            // Regular type
                            var supportsPrimaryConstructorParameters = ShouldSupportPrimaryConstructorParameters(symbol);
                            var constructorParameters = ImmutableArray<IParameterSymbol>.Empty;
                            if (supportsPrimaryConstructorParameters)
                            {
                                if (symbol.IsRecord)
                                {
                                    // If there is a primary constructor then that will be declared before the copy constructor
                                    // A record always generates a copy constructor and marks it as implicitly declared
                                    // todo: find an alternative to this magic
                                    var potentialPrimaryConstructor = symbol.Constructors[0];
                                    if (!potentialPrimaryConstructor.IsImplicitlyDeclared)
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
                                }
                            }

                            var implicitMemberSelectionStrategy = (_options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol)) switch
                            {
                                (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
                                (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
                                _  => GenerateFieldIds.None
                            };
                            var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, LibraryTypes);
                            if (!fieldIdAssignmentHelper.IsValidForSerialization)
                            {
                                throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason));
                            }

                            var typeDescription = new SerializableTypeDescription(semanticModel, symbol, supportsPrimaryConstructorParameters && constructorParameters.Length > 0, GetDataMembers(fieldIdAssignmentHelper), LibraryTypes);
                            metadataModel.SerializableTypes.Add(typeDescription);
                        }
                    }

                    if (symbol.TypeKind == TypeKind.Interface)
                    {
                        var attribute = HasAttribute(
                            symbol,
                            LibraryTypes.GenerateMethodSerializersAttribute,
                            inherited: true);
                        if (attribute != null)
                        {
                            var prop = symbol.GetAllMembers<IPropertySymbol>().FirstOrDefault();
                            if (prop is { })
                            {
                                throw new OrleansGeneratorDiagnosticAnalysisException(RpcInterfacePropertyDiagnostic.CreateDiagnostic(symbol, prop));
                            }

                            var baseClass = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value;
                            var isExtension = (bool)attribute.ConstructorArguments[1].Value;
                            var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseTypeInvokableBaseTypes, baseClass);

                            var description = new InvokableInterfaceDescription(
                                this,
                                semanticModel,
                                symbol,
                                GetAlias(symbol) ?? symbol.Name,
                                baseClass,
                                isExtension,
                                invokableBaseTypes);
                            metadataModel.InvokableInterfaces.Add(description);
                        }
                    }

                    if ((symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct) && !symbol.IsAbstract && (symbol.DeclaredAccessibility == Accessibility.Public || symbol.DeclaredAccessibility == Accessibility.Internal))
                    {
                        if (symbol.HasAttribute(LibraryTypes.RegisterSerializerAttribute))
                        {
                            metadataModel.DetectedSerializers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterActivatorAttribute))
                        {
                            metadataModel.DetectedActivators.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterCopierAttribute))
                        {
                            metadataModel.DetectedCopiers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterConverterAttribute))
                        {
                            metadataModel.DetectedConverters.Add(symbol);
                        }

                        // Find all implementations of invokable interfaces
                        foreach (var iface in symbol.AllInterfaces)
                        {
                            var attribute = HasAttribute(
                                iface,
                                LibraryTypes.GenerateMethodSerializersAttribute,
                                inherited: true);
                            if (attribute != null)
                            {
                                metadataModel.InvokableInterfaceImplementations.Add(symbol);
                                break;
                            }
                        }
                    }

                    GenerateFieldIds GetGenerateFieldIdsOptionFromType(INamedTypeSymbol t)
                    {
                        var attribute = t.GetAttribute(LibraryTypes.GenerateSerializerAttribute);
                        if (attribute == null)
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

                    bool ShouldGenerateSerializer(INamedTypeSymbol t)
                    {
                        if (t.HasAttribute(LibraryTypes.GenerateSerializerAttribute))
                        {
                            return true;
                        }

                        foreach (var attr in _generateSerializerAttributes)
                        {
                            if (HasAttribute(t, attr, inherited: true) != null)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool ShouldSupportPrimaryConstructorParameters(INamedTypeSymbol t)
                    {
                        static bool TestGenerateSerializerAttribute(INamedTypeSymbol t, INamedTypeSymbol at)
                        {
                            var attribute = t.GetAttribute(at);
                            if (attribute != null)
                            {
                                foreach (var namedArgument in attribute.NamedArguments)
                                {
                                    if (namedArgument.Key == "IncludePrimaryConstructorParameters")
                                    {
                                        if (namedArgument.Value.Kind == TypedConstantKind.Primitive && namedArgument.Value.Value is bool b && b == false)
                                        {
                                            return false;
                                        }
                                    }
                                }
                            }

                            return true;
                        }

                        if (!TestGenerateSerializerAttribute(t, LibraryTypes.GenerateSerializerAttribute))
                        {
                            return false;
                        }

                        foreach (var attr in _generateSerializerAttributes)
                        {
                            if (!TestGenerateSerializerAttribute(t, attr))
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                }
            }

            return metadataModel;

            Dictionary<INamedTypeSymbol, INamedTypeSymbol> GetInvokableBaseTypes(Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>> proxyBaseTypeInvokableBaseTypes, INamedTypeSymbol baseClass)
            {
                // Set the base invokable types which are used if attributes on individual methods do not override them.
                if (!proxyBaseTypeInvokableBaseTypes.TryGetValue(baseClass, out var invokableBaseTypes))
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

                    proxyBaseTypeInvokableBaseTypes[baseClass] = invokableBaseTypes;
                }

                return invokableBaseTypes;
            }

            void ComputeAssembliesToExamine(IAssemblySymbol asm, HashSet<IAssemblySymbol> expandedAssemblies)
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
                    ComputeAssembliesToExamine(type.OriginalDefinition.ContainingAssembly, expandedAssemblies);
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

        public string GetAlias(ISymbol symbol)
        {
            return (string)symbol.GetAttribute(LibraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;
        }

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

        // Returns true if the type declaration has the specified attribute.
        private static AttributeData HasAttribute(INamedTypeSymbol symbol, INamedTypeSymbol attributeType, bool inherited)
        {
            if (symbol.GetAttribute(attributeType) is { } attribute)
                return attribute;

            if (inherited)
            {
                foreach (var iface in symbol.AllInterfaces)
                {
                    if (iface.GetAttribute(attributeType) is { } iattr)
                        return iattr;
                }

                while ((symbol = symbol.BaseType) != null)
                {
                    if (symbol.GetAttribute(attributeType) is { } attr)
                        return attr;
                }
            }

            return null;
        }

        internal static AttributeSyntax GetGeneratedCodeAttributeSyntax() => GeneratedCodeAttributeSyntax;
        private static readonly AttributeSyntax GeneratedCodeAttributeSyntax =
                Attribute(ParseName("global::System.CodeDom.Compiler.GeneratedCodeAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
                        AttributeArgument(typeof(CodeGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression()));

        internal static AttributeSyntax GetMethodImplAttributeSyntax() => MethodImplAttributeSyntax;
        private static readonly AttributeSyntax MethodImplAttributeSyntax =
            Attribute(ParseName("global::System.Runtime.CompilerServices.MethodImplAttribute"))
                .AddArgumentListArguments(AttributeArgument(ParseName("global::System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));
    }
}
