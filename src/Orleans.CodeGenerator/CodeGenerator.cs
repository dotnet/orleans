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
                    AddMember(ns, invokable);

                    var methodSymbol = method.Method;
                    if (GetWellKnownTypeId(methodSymbol) is uint wellKnownTypeId)
                    {
                        metadataModel.WellKnownTypeIds.Add((generatedInvokerDescription.OpenTypeSyntax, wellKnownTypeId));
                    }

                    if (GetTypeAlias(methodSymbol) is string typeAlias)
                    {
                        metadataModel.TypeAliases.Add((generatedInvokerDescription.OpenTypeSyntax, typeAlias));
                    }
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
                var copier = CopierGenerator.GenerateCopier(LibraryTypes, type);
                AddMember(ns, copier);

                if (!type.IsEnumType && (!type.IsValueType && type.IsEmptyConstructable && type is not GeneratedInvokerDescription || type.HasActivatorConstructor))
                {
                    metadataModel.ActivatableTypes.Add(type);

                    // Generate a partial serializer class for each serializable type.
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

#pragma warning disable RS1024 // Compare symbols correctly
            var referencedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly

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
#pragma warning disable RS1024 // Compare symbols correctly
            var proxyBaseTypeInvokableBaseTypes = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly

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

                    if (GetTypeAlias(symbol) is string typeAlias)
                    {
                        metadataModel.TypeAliases.Add((symbol.ToOpenTypeSyntax(), typeAlias));
                    }

                    if (FSharpUtilities.IsUnionCase(LibraryTypes, symbol, out var sumType) && ShouldGenerateSerializer(sumType))
                    {
                        var typeDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(semanticModel, symbol, LibraryTypes);
                        metadataModel.SerializableTypes.Add(typeDescription);
                    }
                    else if (ShouldGenerateSerializer(symbol))
                    {
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
                                    var annotatedConstructors = symbol.Constructors.Where(ctor => LibraryTypes.ConstructorAttributeTypes.Any(ctor.HasAttribute)).ToList();
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
                                throw new InvalidOperationException($"Implicit field ids cannot be generated for type {symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}: {fieldIdAssignmentHelper.FailureReason}.");
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
                                throw new InvalidOperationException($"Invokable type {symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} contains property {prop.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}. Invokable types cannot contain properties.");
                            }

                            var baseClass = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value;
                            var isExtension = (bool)attribute.ConstructorArguments[1].Value;
                            var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseTypeInvokableBaseTypes, baseClass);

                            var description = new InvokableInterfaceDescription(
                                this,
                                semanticModel,
                                symbol,
                                GetTypeAlias(symbol) ?? symbol.Name,
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
                        var attribute = HasAttribute(t, LibraryTypes.GenerateSerializerAttribute);
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
                        if (!semanticModel.IsAccessible(0, t))
                        {
                            return false;
                        }

                        if (HasAttribute(t, LibraryTypes.GenerateSerializerAttribute) != null)
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
                            var attribute = HasAttribute(t, at);
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
#pragma warning disable RS1024 // Compare symbols correctly
                    invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly

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
            var members = new Dictionary<(ushort, bool), IMemberDescription>();

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

        public ushort? GetId(ISymbol memberSymbol) => GetId(LibraryTypes, memberSymbol);

        internal static ushort? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
        {
            var idAttr = memberSymbol.GetAttributes().FirstOrDefault(attr => libraryTypes.IdAttributeTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, attr.AttributeClass)));
            if (idAttr is null)
            {
                return null;
            }

            var id = (ushort)idAttr.ConstructorArguments.First().Value;
            return id;
        }

        private uint? GetWellKnownTypeId(ISymbol symbol)
        {
            var attr = symbol.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(LibraryTypes.WellKnownIdAttribute, attr.AttributeClass));
            if (attr is null)
            {
                return null;
            }

            var id = (uint)attr.ConstructorArguments.First().Value;
            return id;
        }

        private string GetTypeAlias(ISymbol symbol)
        {
            var attr = symbol.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(LibraryTypes.WellKnownAliasAttribute, attr.AttributeClass));
            if (attr is null)
            {
                return null;
            }

            var value = (string)attr.ConstructorArguments.First().Value;
            return value;
        }

        // Returns true if the type declaration has the specified attribute.
        private static AttributeData HasAttribute(INamedTypeSymbol symbol, ISymbol attributeType, bool inherited = false)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                {
                    return attribute;
                }
            }

            if (inherited)
            {
                foreach (var iface in symbol.AllInterfaces)
                {
                    foreach (var attribute in iface.GetAttributes())
                    {
                        if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                        {
                            return attribute;
                        }
                    }
                }

                while ((symbol = symbol.BaseType) != null)
                {
                    foreach (var attribute in symbol.GetAttributes())
                    {
                        if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                        {
                            return attribute;
                        }
                    }
                }
            }

            return null;
        }

        internal static AttributeSyntax GetGeneratedCodeAttributeSyntax()
        {
            var version = typeof(CodeGenerator).Assembly.GetName().Version.ToString();
            return
                Attribute(ParseName("System.CodeDom.Compiler.GeneratedCodeAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
                        AttributeArgument(version.GetLiteralExpression()));
        }

        internal static AttributeSyntax GetMethodImplAttributeSyntax()
        {
            return Attribute(ParseName("System.Runtime.CompilerServices.MethodImplAttribute"))
                .AddArgumentListArguments(AttributeArgument(ParseName("System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));
        }
    }
}
