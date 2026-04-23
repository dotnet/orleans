using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model.Incremental;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    #nullable disable
    internal class MetadataGenerator
    {
        private readonly IGeneratorServices _generatorServices;
        private readonly MetadataAggregateModel _metadataModel;
        private readonly string _assemblyName;

        public MetadataGenerator(IGeneratorServices generatorServices, MetadataAggregateModel metadataModel, string assemblyName)
        {
            _generatorServices = generatorServices;
            _metadataModel = metadataModel;
            _assemblyName = assemblyName ?? generatorServices.Compilation.AssemblyName ?? "Assembly";
        }

        public ClassDeclarationSyntax GenerateMetadata()
            => GenerateIncrementalMetadata();

        private ClassDeclarationSyntax GenerateIncrementalMetadata()
        {
            var configParam = "config".ToIdentifierName();
            var body = new List<StatementSyntax>();
            var model = _metadataModel;
            var orderedProxyInterfaces = GetOrderedProxyInterfaces(model.ProxyInterfaces.AsImmutableArray());
            var generatedInvokables = GetGeneratedInvokableMetadata(orderedProxyInterfaces);
            var serializableRegistrations = GetOrderedSerializableRegistrations(model, generatedInvokables);

            var addSerializerMethod = configParam.Member("Serializers").Member("Add");
            foreach (var registration in serializableRegistrations)
            {
                AddRegistration(body, addSerializerMethod, registration.SerializerTypeSyntax);
            }

            foreach (var type in model.RegisteredCodecs.AsImmutableArray().Where(static codec => codec.Kind == RegisteredCodecKind.Serializer))
            {
                AddRegistration(body, addSerializerMethod, GetOpenTypeSyntax(type.Type));
            }

            var addCopierMethod = configParam.Member("Copiers").Member("Add");
            foreach (var registration in serializableRegistrations)
            {
                if (registration.CopierTypeSyntax is not null)
                {
                    AddRegistration(body, addCopierMethod, registration.CopierTypeSyntax);
                }
            }

            foreach (var type in model.RegisteredCodecs.AsImmutableArray().Where(static codec => codec.Kind == RegisteredCodecKind.Copier))
            {
                AddRegistration(body, addCopierMethod, GetOpenTypeSyntax(type.Type));
            }

            var addConverterMethod = configParam.Member("Converters").Member("Add");
            foreach (var type in model.RegisteredCodecs.AsImmutableArray().Where(static codec => codec.Kind == RegisteredCodecKind.Converter))
            {
                AddRegistration(body, addConverterMethod, GetOpenTypeSyntax(type.Type));
            }

            var addProxyMethod = configParam.Member("InterfaceProxies").Member("Add");
            foreach (var type in orderedProxyInterfaces)
            {
                AddRegistration(body, addProxyMethod, GetGeneratedProxyTypeSyntax(type));
            }

            var addInvokableInterfaceMethod = configParam.Member("Interfaces").Member("Add");
            foreach (var type in orderedProxyInterfaces.Select(static proxy => proxy.InterfaceType).Distinct())
            {
                AddRegistration(body, addInvokableInterfaceMethod, GetOpenTypeSyntax(type));
            }

            var addInvokableInterfaceImplementationMethod = configParam.Member("InterfaceImplementations").Member("Add");
            foreach (var type in GetOrderedInterfaceImplementations(model.InterfaceImplementations.AsImmutableArray()))
            {
                AddRegistration(body, addInvokableInterfaceImplementationMethod, GetOpenTypeSyntax(type.ImplementationType));
            }

            var addActivatorMethod = configParam.Member("Activators").Member("Add");
            foreach (var registration in serializableRegistrations)
            {
                if (registration.ActivatorTypeSyntax is not null)
                {
                    AddRegistration(body, addActivatorMethod, registration.ActivatorTypeSyntax);
                }
            }

            foreach (var type in model.RegisteredCodecs.AsImmutableArray().Where(static codec => codec.Kind == RegisteredCodecKind.Activator))
            {
                AddRegistration(body, addActivatorMethod, GetOpenTypeSyntax(type.Type));
            }

            var addWellKnownTypeIdMethod = configParam.Member("WellKnownTypeIds").Member("Add");
            foreach (var type in model.ReferenceAssemblyData.WellKnownTypeIds.AsImmutableArray())
            {
                body.Add(ExpressionStatement(InvocationExpression(addWellKnownTypeIdMethod,
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Id))),
                        Argument(TypeOfExpression(GetOpenTypeSyntax(type.Type))),
                    })))));
            }

            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            foreach (var type in model.ReferenceAssemblyData.TypeAliases.AsImmutableArray())
            {
                body.Add(ExpressionStatement(InvocationExpression(addTypeAliasMethod,
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(type.Alias))),
                        Argument(TypeOfExpression(GetOpenTypeSyntax(type.Type))),
                    })))));
            }

            AddIncrementalCompoundTypeAliases(configParam, body, generatedInvokables);
            return CreateMetadataClass(body, configParam);
        }

        private ClassDeclarationSyntax CreateMetadataClass(List<StatementSyntax> body, IdentifierNameSyntax configParam)
        {
            var configType = _generatorServices.LibraryTypes.TypeManifestOptions;
            var configureMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "ConfigureInner")
                .AddModifiers(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithType(configType.ToTypeSyntax()))
                .AddBodyStatements(body.ToArray());

            var interfaceType = _generatorServices.LibraryTypes.TypeManifestProviderBase;
            return ClassDeclaration("Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_assemblyName))
                .AddBaseListTypes(SimpleBaseType(interfaceType.ToTypeSyntax()))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(CodeGenerator.GetGeneratedCodeAttributes())
                .AddMembers(configureMethod);
        }

        private void AddIncrementalCompoundTypeAliases(
            IdentifierNameSyntax configParam,
            List<StatementSyntax> body,
            ImmutableArray<GeneratedInvokableMetadata> generatedInvokables)
        {
            var aliases = _metadataModel.ReferenceAssemblyData.CompoundTypeAliases.AsImmutableArray()
                .OrderBy(static entry => entry.Components.Count)
                .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
                .ToImmutableArray();

            var generatedAliases = generatedInvokables
                .SelectMany(static invokable => invokable.Aliases)
                .ToImmutableArray();

            var aliasTree = IncrementalCompoundTypeAliasTree.Create();
            foreach (var alias in aliases.Concat(generatedAliases))
            {
                aliasTree.Add(alias.Components.AsImmutableArray(), alias.TargetType);
            }

            var nodeId = 0;
            AddIncrementalCompoundTypeAliases(body, configParam.Member("CompoundTypeAliases"), aliasTree, ref nodeId);
        }

        private void AddIncrementalCompoundTypeAliases(
            List<StatementSyntax> body,
            ExpressionSyntax tree,
            IncrementalCompoundTypeAliasTree aliases,
            ref int nodeId)
        {
            ExpressionSyntax node;
            if (!aliases.HasKey)
            {
                node = tree;
            }
            else
            {
                var nodeName = IdentifierName($"n{++nodeId}");
                node = nodeName;
                var addArguments = new List<ArgumentSyntax>(2) { CreateCompoundAliasArgument(aliases.Key) };
                if (aliases.HasValue)
                {
                    addArguments.Add(Argument(TypeOfExpression(GetOpenTypeSyntax(aliases.Value))));
                }

                if (aliases.Children is { Count: > 0 })
                {
                    body.Add(LocalDeclarationStatement(VariableDeclaration(
                        ParseTypeName("var"),
                        SingletonSeparatedList(VariableDeclarator(nodeName.Identifier).WithInitializer(EqualsValueClause(InvocationExpression(
                            tree.Member("Add"),
                            ArgumentList(SeparatedList(addArguments)))))))));
                }
                else
                {
                    body.Add(ExpressionStatement(InvocationExpression(tree.Member("Add"), ArgumentList(SeparatedList(addArguments)))));
                }
            }

            if (aliases.Children is { Count: > 0 })
            {
                foreach (var child in aliases.Children.Values)
                {
                    AddIncrementalCompoundTypeAliases(body, node, child, ref nodeId);
                }
            }
        }

        private ArgumentSyntax CreateCompoundAliasArgument(CompoundAliasComponentModel component)
            => component.IsType
                ? Argument(TypeOfExpression(GetOpenTypeSyntax(component.TypeValue)))
                : Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(component.StringValue)));

        private ImmutableArray<ProxyInterfaceModel> GetOrderedProxyInterfaces(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
            => OrderBySourceLocation(proxyInterfaces, static proxy => proxy.InterfaceType, static proxy => proxy.InterfaceType.SyntaxString);

        private ImmutableArray<InterfaceImplementationModel> GetOrderedInterfaceImplementations(ImmutableArray<InterfaceImplementationModel> interfaceImplementations)
            => OrderBySourceLocation(interfaceImplementations, static implementation => implementation.ImplementationType, static implementation => implementation.ImplementationType.SyntaxString);

        private ImmutableArray<T> OrderBySourceLocation<T>(ImmutableArray<T> entries, Func<T, TypeRef> typeSelector, Func<T, string> sortKeySelector)
        {
            return entries
                .Select(entry =>
                {
                    var sourceLocation = TryResolveOpenNamedType(typeSelector(entry), out var symbol)
                        ? symbol.Locations.FirstOrDefault(static location => location.IsInSource)
                        : null;

                    return (
                        Entry: entry,
                        SourceOrderGroup: sourceLocation is null ? 1 : 0,
                        FilePath: sourceLocation?.SourceTree?.FilePath ?? string.Empty,
                        Position: sourceLocation?.SourceSpan.Start ?? int.MaxValue,
                        SortKey: sortKeySelector(entry));
                })
                .OrderBy(static entry => entry.SourceOrderGroup)
                .ThenBy(static entry => entry.FilePath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Position)
                .ThenBy(static entry => entry.SortKey, StringComparer.Ordinal)
                .Select(static entry => entry.Entry)
                .ToImmutableArray();
        }

        private ImmutableArray<GeneratedInvokableMetadata> GetGeneratedInvokableMetadata(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        {
            var result = ImmutableArray.CreateBuilder<GeneratedInvokableMetadata>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var proxy in proxyInterfaces)
            {
                foreach (var method in proxy.Methods.AsImmutableArray())
                {
                    var metadata = CreateGeneratedInvokableMetadata(proxy, method);
                    var key = metadata.TypeSyntax.ToString();
                    if (seen.Add(key))
                    {
                        result.Add(metadata);
                    }
                }
            }

            return result.ToImmutable();
        }

        private ImmutableArray<SerializableMetadataRegistration> GetOrderedSerializableRegistrations(
            MetadataAggregateModel model,
            ImmutableArray<GeneratedInvokableMetadata> generatedInvokables)
        {
            var registrations = new List<SerializableMetadataRegistration>(model.SerializableTypes.Count + generatedInvokables.Length);

            foreach (var type in model.SerializableTypes.AsImmutableArray())
            {
                TypeSyntax copierType = null;
                if (!type.IsEnumType)
                {
                    copierType = TryGetDefaultCopierType(type.TypeSyntax, model.DefaultCopiers)
                        ?? GetCopierTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count);
                }

                var activatorType = ShouldGenerateActivator(type)
                    ? GetActivatorTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count)
                    : null;

                registrations.Add(new SerializableMetadataRegistration(
                    sourceType: type.TypeSyntax,
                    sortKey: type.TypeSyntax.SyntaxString,
                    serializerTypeSyntax: GetCodecTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count),
                    copierTypeSyntax: copierType,
                    activatorTypeSyntax: activatorType));
            }

            foreach (var type in generatedInvokables)
            {
                registrations.Add(new SerializableMetadataRegistration(
                    sourceType: type.SourceType,
                    sortKey: type.TypeSyntax.ToString(),
                    serializerTypeSyntax: type.CodecTypeSyntax,
                    copierTypeSyntax: type.CopierTypeSyntax,
                    activatorTypeSyntax: null));
            }

            return registrations
                .Select(registration =>
                {
                    var sourceLocation = TryResolveOpenNamedType(registration.SourceType, out var symbol)
                        ? symbol.Locations.FirstOrDefault(static location => location.IsInSource)
                        : null;
                    return (
                        Registration: registration,
                        SourceOrderGroup: sourceLocation is null ? 1 : 0,
                        FilePath: sourceLocation?.SourceTree?.FilePath ?? string.Empty,
                        Position: sourceLocation?.SourceSpan.Start ?? int.MaxValue);
                })
                .OrderBy(static entry => entry.SourceOrderGroup)
                .ThenBy(static entry => entry.FilePath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Position)
                .ThenBy(static entry => entry.Registration.SortKey, StringComparer.Ordinal)
                .Select(static entry => entry.Registration)
                .ToImmutableArray();
        }

        private GeneratedInvokableMetadata CreateGeneratedInvokableMetadata(ProxyInterfaceModel proxy, MethodModel method)
        {
            var generatedNamespace = method.ContainingInterfaceGeneratedNamespace;
            var name = GetGeneratedInvokableClassName(proxy, method);
            var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Count;
            var typeSyntax = CreateGeneratedTypeSyntax(generatedNamespace, name, genericArity);
            var targetType = new TypeRef(typeSyntax.ToString());
            var aliases = CreateGeneratedInvokableAliases(proxy, method, targetType);
            var codecTypeSyntax = GetCodecTypeName(generatedNamespace, name, genericArity);
            var copierTypeSyntax = GetCopierTypeName(generatedNamespace, name, genericArity);

            return new GeneratedInvokableMetadata(
                typeSyntax,
                method.ContainingInterfaceType,
                aliases,
                codecTypeSyntax,
                copierTypeSyntax);
        }

        private static string GetGeneratedInvokableClassName(ProxyInterfaceModel proxy, MethodModel method)
        {
            var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Count;
            var typeArgs = genericArity > 0 ? "_" + genericArity : string.Empty;
            return $"Invokable_{method.ContainingInterfaceName}_{proxy.ProxyBase.GeneratedClassNameComponent}_{method.GeneratedMethodId}{typeArgs}";
        }

        private static ImmutableArray<CompoundTypeAliasModel> CreateGeneratedInvokableAliases(
            ProxyInterfaceModel proxy,
            MethodModel method,
            TypeRef targetType)
        {
            var result = ImmutableArray.CreateBuilder<CompoundTypeAliasModel>(2);
            if (!string.Equals(method.MethodId, method.GeneratedMethodId, StringComparison.Ordinal))
            {
                result.Add(CreateGeneratedInvokableAlias(proxy, method, method.MethodId, targetType));
            }

            result.Add(CreateGeneratedInvokableAlias(proxy, method, method.GeneratedMethodId, targetType));
            return result.ToImmutable();
        }

        private static CompoundTypeAliasModel CreateGeneratedInvokableAlias(
            ProxyInterfaceModel proxy,
            MethodModel method,
            string methodId,
            TypeRef targetType)
        {
            var components = ImmutableArray.CreateBuilder<CompoundAliasComponentModel>(proxy.ProxyBase.IsExtension ? 6 : 4);
            components.Add(new CompoundAliasComponentModel("inv"));
            components.Add(new CompoundAliasComponentModel(proxy.ProxyBase.ProxyBaseType));
            if (proxy.ProxyBase.IsExtension)
            {
                components.Add(new CompoundAliasComponentModel("Ext"));
            }

            components.Add(new CompoundAliasComponentModel(proxy.ProxyBase.IsExtension ? proxy.InterfaceType : method.ContainingInterfaceType));

            if (proxy.ProxyBase.IsExtension)
            {
                components.Add(new CompoundAliasComponentModel(method.OriginalContainingInterfaceType));
            }

            components.Add(new CompoundAliasComponentModel(methodId));
            return new CompoundTypeAliasModel(new EquatableArray<CompoundAliasComponentModel>(components.MoveToImmutable()), targetType);
        }

        private bool TryResolveOpenNamedType(TypeRef typeRef, out INamedTypeSymbol symbol)
        {
            var target = NormalizeTypeRefSyntax(typeRef.SyntaxString);

            foreach (var assembly in EnumerateAssemblies(_generatorServices.Compilation))
            {
                foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
                {
                    var candidate = type.OriginalDefinition;
                    if (string.Equals(
                        NormalizeTypeRefSyntax(candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                        target,
                        StringComparison.Ordinal))
                    {
                        symbol = candidate;
                        return true;
                    }
                }
            }

            symbol = null;
            return false;
        }

        private TypeSyntax GetOpenTypeSyntax(TypeRef typeRef)
            => TryResolveOpenNamedType(typeRef, out var symbol) ? symbol.ToOpenTypeSyntax() : typeRef.ToTypeSyntax();

        private TypeSyntax GetGeneratedProxyTypeSyntax(ProxyInterfaceModel proxy)
        {
            var genericArity = TryResolveOpenNamedType(proxy.InterfaceType, out var symbol)
                ? symbol.GetAllTypeParameters().Count()
                : proxy.TypeParameters.Count;

            return CreateGeneratedTypeSyntax(proxy.GeneratedNamespace, ProxyGenerator.GetSimpleClassName(proxy.Name), genericArity);
        }

        private static TypeSyntax TryGetDefaultCopierType(TypeRef originalType, EquatableArray<DefaultCopierModel> defaultCopiers)
        {
            foreach (var copier in defaultCopiers.AsImmutableArray())
            {
                if (copier.OriginalType.Equals(originalType))
                {
                    return copier.CopierType.ToTypeSyntax();
                }
            }

            return null;
        }

        private static void AddRegistration(List<StatementSyntax> body, ExpressionSyntax addMethod, TypeSyntax typeSyntax)
        {
            body.Add(ExpressionStatement(InvocationExpression(addMethod,
                ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(typeSyntax)))))));
        }

        private static bool ShouldGenerateActivator(SerializableTypeModel type)
            => !type.IsAbstractType
                && !type.IsEnumType
                && ((!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator) || type.HasActivatorConstructor);

        private static IEnumerable<IAssemblySymbol> EnumerateAssemblies(Compilation compilation)
        {
            yield return compilation.Assembly;

            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                yield return assembly;
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol @namespace)
        {
            foreach (var member in @namespace.GetMembers())
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    foreach (var nestedType in EnumerateTypes(childNamespace))
                    {
                        yield return nestedType;
                    }

                    continue;
                }

                if (member is INamedTypeSymbol type)
                {
                    foreach (var nested in EnumerateTypeAndNestedTypes(type))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNestedTypes(INamedTypeSymbol type)
        {
            yield return type;

            foreach (var nested in type.GetTypeMembers())
            {
                foreach (var child in EnumerateTypeAndNestedTypes(nested))
                {
                    yield return child;
                }
            }
        }

        private static string NormalizeTypeRefSyntax(string syntaxString)
            => syntaxString.StartsWith("global::", StringComparison.Ordinal)
                ? syntaxString.Substring("global::".Length)
                : syntaxString;

        public static TypeSyntax GetCodecTypeName(ISerializableTypeDescription type)
            => GetCodecTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count);

        public static TypeSyntax GetCodecTypeName(string generatedNamespace, string name, int genericArity)
            => CreateGeneratedTypeSyntax(generatedNamespace, SerializerGenerator.GetSimpleClassName(name), genericArity);

        public static TypeSyntax GetCopierTypeName(ISerializableTypeDescription type)
            => GetCopierTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count);

        public static TypeSyntax GetCopierTypeName(string generatedNamespace, string name, int genericArity)
            => CreateGeneratedTypeSyntax(generatedNamespace, CopierGenerator.GetSimpleClassName(name), genericArity);

        public static TypeSyntax GetActivatorTypeName(ISerializableTypeDescription type)
            => GetActivatorTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Count);

        public static TypeSyntax GetActivatorTypeName(string generatedNamespace, string name, int genericArity)
            => CreateGeneratedTypeSyntax(generatedNamespace, ActivatorGenerator.GetSimpleClassName(name), genericArity);

        private static TypeSyntax CreateGeneratedTypeSyntax(string generatedNamespace, string simpleName, int genericArity)
        {
            var name = genericArity > 0 ? $"{simpleName}<{new string(',', genericArity - 1)}>" : simpleName;
            return ParseTypeName($"{generatedNamespace}.{name}");
        }

        private readonly struct GeneratedInvokableMetadata
        {
            public GeneratedInvokableMetadata(
                TypeSyntax typeSyntax,
                TypeRef sourceType,
                ImmutableArray<CompoundTypeAliasModel> aliases,
                TypeSyntax codecTypeSyntax,
                TypeSyntax copierTypeSyntax)
            {
                TypeSyntax = typeSyntax;
                SourceType = sourceType;
                Aliases = aliases;
                CodecTypeSyntax = codecTypeSyntax;
                CopierTypeSyntax = copierTypeSyntax;
            }

            public TypeSyntax TypeSyntax { get; }
            public TypeRef SourceType { get; }
            public ImmutableArray<CompoundTypeAliasModel> Aliases { get; }
            public TypeSyntax CodecTypeSyntax { get; }
            public TypeSyntax CopierTypeSyntax { get; }
        }

        private sealed class IncrementalCompoundTypeAliasTree
        {
            private Dictionary<CompoundAliasComponentModel, IncrementalCompoundTypeAliasTree> _children;
            private TypeRef _value;
            private bool _hasValue;

            private IncrementalCompoundTypeAliasTree(CompoundAliasComponentModel key, TypeRef value, bool hasKey, bool hasValue)
            {
                Key = key;
                _value = value;
                HasKey = hasKey;
                _hasValue = hasValue;
            }

            public static IncrementalCompoundTypeAliasTree Create() => new(default, TypeRef.Empty, hasKey: false, hasValue: false);

            public CompoundAliasComponentModel Key { get; }
            public bool HasKey { get; }
            public bool HasValue => _hasValue;
            public TypeRef Value => _value;
            public Dictionary<CompoundAliasComponentModel, IncrementalCompoundTypeAliasTree> Children => _children;

            public void Add(ImmutableArray<CompoundAliasComponentModel> keys, TypeRef value) => Add(keys.AsSpan(), value);

            public void Add(ReadOnlySpan<CompoundAliasComponentModel> keys, TypeRef value)
            {
                if (keys.Length == 0)
                {
                    throw new InvalidOperationException("No valid key specified.");
                }

                var key = keys[0];
                if (keys.Length == 1)
                {
                    AddInternal(key, value, hasValue: true);
                }
                else
                {
                    var childNode = GetChildOrDefault(key) ?? AddInternal(key, TypeRef.Empty, hasValue: false);
                    childNode.Add(keys.Slice(1), value);
                }
            }

            private IncrementalCompoundTypeAliasTree GetChildOrDefault(CompoundAliasComponentModel key)
            {
                TryGetChild(key, out var result);
                return result;
            }

            private bool TryGetChild(CompoundAliasComponentModel key, out IncrementalCompoundTypeAliasTree result)
            {
                if (_children is { } children)
                {
                    return children.TryGetValue(key, out result);
                }

                result = default;
                return false;
            }

            private IncrementalCompoundTypeAliasTree AddInternal(CompoundAliasComponentModel key, TypeRef value, bool hasValue)
            {
                _children ??= new();

                if (_children.TryGetValue(key, out var existing))
                {
                    if (hasValue)
                    {
                        if (existing._hasValue && !existing._value.Equals(value))
                        {
                            throw new ArgumentException($"A key with the value '{key}' already exists.");
                        }

                        existing._value = value;
                        existing._hasValue = true;
                    }

                    return existing;
                }

                var child = new IncrementalCompoundTypeAliasTree(key, value, hasKey: true, hasValue: hasValue);
                _children.Add(key, child);
                return child;
            }
        }

        private readonly struct SerializableMetadataRegistration
        {
            public SerializableMetadataRegistration(
                TypeRef sourceType,
                string sortKey,
                TypeSyntax serializerTypeSyntax,
                TypeSyntax copierTypeSyntax,
                TypeSyntax activatorTypeSyntax)
            {
                SourceType = sourceType;
                SortKey = sortKey;
                SerializerTypeSyntax = serializerTypeSyntax;
                CopierTypeSyntax = copierTypeSyntax;
                ActivatorTypeSyntax = activatorTypeSyntax;
            }

            public TypeRef SourceType { get; }
            public string SortKey { get; }
            public TypeSyntax SerializerTypeSyntax { get; }
            public TypeSyntax CopierTypeSyntax { get; }
            public TypeSyntax ActivatorTypeSyntax { get; }
        }
    }
}
