using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    public class CodeGeneratorOptions
    {
        public List<string> GenerateSerializerAttributes { get; } = new() { "Orleans.GenerateSerializer" };
        public List<string> IdAttributes { get; } = new() { "Orleans.IdAttribute" };
        public List<string> AliasAttributes { get; } = new() { "Orleans.AliasAttribute" };
        public List<string> ImmutableAttributes { get; } = new() { "Orleans.ImmutableAttribute" };

        public bool GenerateFieldIds { get; set; } = false;
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

                if (type.IsEmptyConstructable || type.HasActivatorConstructor)
                {
                    metadataModel.ActivatableTypes.Add(type);

                    // Generate a partial serializer class for each serializable type.
                    var activator = ActivatorGenerator.GenerateActivator(LibraryTypes, type);
                    AddMember(ns, activator);
                }
            }

            // Generate metadata.
            var metadataClassNamespace = CodeGeneratorName + "." + _compilation.AssemblyName;
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
                            var typeDescription = new SerializableTypeDescription(semanticModel, symbol, GetDataMembers(symbol), LibraryTypes);
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

                    bool ShouldGenerateSerializer(INamedTypeSymbol t)
                    {
                        if (!semanticModel.IsAccessible(0, t))
                        {
                            return false;
                        }

                        if (HasAttribute(t, LibraryTypes.GenerateSerializerAttribute, inherited: true) != null)
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

        private static IEnumerable<MemberDeclarationSyntax> GetTypeDeclarations(SyntaxNode node)
        {
            SyntaxList<MemberDeclarationSyntax> members;
            switch (node)
            {
                case EnumDeclarationSyntax enumDecl:
                    yield return enumDecl;
                    members = new SyntaxList<MemberDeclarationSyntax>();
                    break;
                case TypeDeclarationSyntax type:
                    yield return type;
                    members = type.Members;
                    break;
                case NamespaceDeclarationSyntax ns:
                    members = ns.Members;
                    break;
                case CompilationUnitSyntax compilationUnit:
                    members = compilationUnit.Members;
                    break;
                default:
                    yield break;
            }

            foreach (var member in members)
            {
                foreach (var decl in GetTypeDeclarations(member))
                {
                    yield return decl;
                }
            }
        }

        // Returns descriptions of all data members (fields and properties) 
        private IEnumerable<IMemberDescription> GetDataMembers(INamedTypeSymbol symbol)
        {
            var members = new Dictionary<ushort, IMemberDescription>();
            var hasAttributes = false;
            foreach (var member in symbol.GetMembers())
            {
                if (member.IsStatic || member.IsAbstract)
                {
                    continue;
                }

                if (member.HasAttribute(LibraryTypes.NonSerializedAttribute))
                {
                    continue;
                }

                if (LibraryTypes.IdAttributeTypes.Any(t => member.HasAttribute(t)))
                {
                    hasAttributes = true;
                    break;
                }
            }

            var nextFieldId = (ushort)0;
            foreach (var member in symbol.GetMembers().OrderBy(m => m.MetadataName))
            {
                if (member.IsStatic || member.IsAbstract)
                {
                    continue;
                }

                // Only consider fields and properties.
                if (!(member is IFieldSymbol || member is IPropertySymbol))
                {
                    continue;
                }

                if (member.HasAttribute(LibraryTypes.NonSerializedAttribute))
                {
                    continue;
                }

                if (member is IPropertySymbol prop)
                {
                    var id = GetId(prop);
                    if (!id.HasValue)
                    {
                        if (hasAttributes || !_options.GenerateFieldIds)
                        {
                            continue;
                        }

                        id = ++nextFieldId;
                    }

                    // FieldDescription takes precedence over PropertyDescription
                    if (!members.TryGetValue(id.Value, out var existing))
                    {
                        members[id.Value] = new PropertyDescription(id.Value, prop);
                    }
                }

                if (member is IFieldSymbol field)
                {
                    var id = GetId(field);
                    if (!id.HasValue)
                    {
                        prop = PropertyUtility.GetMatchingProperty(field);

                        if (prop is null)
                        {
                            continue;
                        }

                        if (prop.HasAttribute(LibraryTypes.NonSerializedAttribute))
                        {
                            continue;
                        }

                        id = GetId(prop);
                    }

                    if (!id.HasValue)
                    {
                        if (hasAttributes || !_options.GenerateFieldIds)
                        {
                            continue;
                        }

                        id = nextFieldId++;
                    }

                    // FieldDescription takes precedence over PropertyDescription
                    if (!members.TryGetValue(id.Value, out var existing) || existing is PropertyDescription)
                    {
                        members[id.Value] = new FieldDescription(id.Value, field);
                        continue;
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