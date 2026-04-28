using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    #nullable disable
    internal class MetadataGenerator
    {
        private static readonly TypeSyntax TypeManifestOptionsType = ParseTypeName("global::Orleans.Serialization.Configuration.TypeManifestOptions");
        private static readonly TypeSyntax TypeManifestProviderBaseType = ParseTypeName("global::Orleans.Serialization.Configuration.TypeManifestProviderBase");

        private readonly MetadataAggregateModel _metadataModel;
        private readonly string _assemblyName;

        public MetadataGenerator(MetadataAggregateModel metadataModel, string assemblyName)
        {
            _metadataModel = metadataModel;
            _assemblyName = assemblyName ?? "Assembly";
        }

        public ClassDeclarationSyntax GenerateMetadata()
            => GenerateIncrementalMetadata();

        private ClassDeclarationSyntax GenerateIncrementalMetadata()
        {
            var configParam = "config".ToIdentifierName();
            var body = new List<StatementSyntax>();
            var model = _metadataModel;
            var orderedProxyInterfaces = GetOrderedProxyInterfaces(model.ProxyInterfaces);
            var generatedInvokables = GetGeneratedInvokableMetadata(orderedProxyInterfaces);
            var serializableRegistrations = GetOrderedSerializableRegistrations(model, generatedInvokables);

            var addSerializerMethod = configParam.Member("Serializers").Member("Add");
            foreach (var registration in serializableRegistrations)
            {
                AddRegistration(body, addSerializerMethod, registration.SerializerTypeSyntax);
            }

            foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Serializer))
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

            foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Copier))
            {
                AddRegistration(body, addCopierMethod, GetOpenTypeSyntax(type.Type));
            }

            var addConverterMethod = configParam.Member("Converters").Member("Add");
            foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Converter))
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
            foreach (var type in GetOrderedInterfaceImplementations(model.InterfaceImplementations))
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

            foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Activator))
            {
                AddRegistration(body, addActivatorMethod, GetOpenTypeSyntax(type.Type));
            }

            var addWellKnownTypeIdMethod = configParam.Member("WellKnownTypeIds").Member("Add");
            foreach (var type in model.ReferenceAssemblyData.WellKnownTypeIds)
            {
                body.Add(ExpressionStatement(InvocationExpression(addWellKnownTypeIdMethod,
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(type.Id))),
                        Argument(CreateTypeOfExpression(type.Type)),
                    })))));
            }

            var addTypeAliasMethod = configParam.Member("WellKnownTypeAliases").Member("Add");
            foreach (var type in model.ReferenceAssemblyData.TypeAliases)
            {
                body.Add(ExpressionStatement(InvocationExpression(addTypeAliasMethod,
                    ArgumentList(SeparatedList(new[]
                    {
                        Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(type.Alias))),
                        Argument(CreateTypeOfExpression(type.Type)),
                    })))));
            }

            AddCompoundTypeAliases(configParam, body, generatedInvokables);
            return CreateMetadataClass(body, configParam);
        }

        private ClassDeclarationSyntax CreateMetadataClass(List<StatementSyntax> body, IdentifierNameSyntax configParam)
        {
            var configureMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "ConfigureInner")
                .AddModifiers(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithType(TypeManifestOptionsType))
                .AddBodyStatements(body.ToArray());

            return ClassDeclaration("Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_assemblyName))
                .AddBaseListTypes(SimpleBaseType(TypeManifestProviderBaseType))
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
                .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes())
                .AddMembers(configureMethod);
        }

        private void AddCompoundTypeAliases(
            IdentifierNameSyntax configParam,
            List<StatementSyntax> body,
            ImmutableArray<GeneratedInvokableMetadata> generatedInvokables)
        {
            var aliases = _metadataModel.ReferenceAssemblyData.CompoundTypeAliases
                .OrderBy(static entry => entry.Components.Length)
                .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
                .ToImmutableArray();

            var generatedAliases = generatedInvokables
                .SelectMany(static invokable => invokable.Aliases)
                .ToImmutableArray();

            var aliasTree = CompoundTypeAliasEmissionTree.Create();
            foreach (var alias in aliases.Concat(generatedAliases))
            {
                aliasTree.Add(alias.Components, alias.TargetType);
            }

            var nodeId = 0;
            AddCompoundTypeAliases(body, configParam.Member("CompoundTypeAliases"), aliasTree, ref nodeId);
        }

        private void AddCompoundTypeAliases(
            List<StatementSyntax> body,
            ExpressionSyntax tree,
            CompoundTypeAliasEmissionTree aliases,
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
                    addArguments.Add(Argument(CreateTypeOfExpression(aliases.Value)));
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
                    AddCompoundTypeAliases(body, node, child, ref nodeId);
                }
            }
        }

        private ArgumentSyntax CreateCompoundAliasArgument(CompoundAliasComponentModel component)
            => component.IsType
                ? Argument(CreateTypeOfExpression(component.TypeValue))
                : Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(component.StringValue)));

        private ImmutableArray<ProxyInterfaceModel> GetOrderedProxyInterfaces(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
            => OrderBySourceLocation(proxyInterfaces, static proxy => proxy.SourceLocation, static proxy => proxy.InterfaceType.SyntaxString);

        private ImmutableArray<InterfaceImplementationModel> GetOrderedInterfaceImplementations(ImmutableArray<InterfaceImplementationModel> interfaceImplementations)
            => OrderBySourceLocation(interfaceImplementations, static implementation => implementation.SourceLocation, static implementation => implementation.ImplementationType.SyntaxString);

        private static ImmutableArray<T> OrderBySourceLocation<T>(
            ImmutableArray<T> entries,
            Func<T, SourceLocationModel> locationSelector,
            Func<T, string> sortKeySelector)
        {
            return entries
                .Select(entry =>
                {
                    var sourceLocation = locationSelector(entry);
                    return (
                        Entry: entry,
                        sourceLocation.SourceOrderGroup,
                        sourceLocation.FilePath,
                        sourceLocation.Position,
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
                foreach (var method in proxy.Methods)
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
            var registrations = new List<SerializableMetadataRegistration>(model.SerializableTypes.Length + generatedInvokables.Length);

            foreach (var type in model.SerializableTypes)
            {
                TypeSyntax copierType = null;
                if (!type.IsEnumType)
                {
                    copierType = TryGetDefaultCopierType(type.TypeSyntax, model.DefaultCopiers)
                        ?? GetCopierTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Length);
                }

                var activatorType = ShouldGenerateActivator(type)
                    ? GetActivatorTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Length)
                    : null;

                registrations.Add(new SerializableMetadataRegistration(
                    sourceType: type.TypeSyntax,
                    sortKey: type.TypeSyntax.SyntaxString,
                    serializerTypeSyntax: GetCodecTypeName(type.GeneratedNamespace, type.Name, type.TypeParameters.Length),
                    copierTypeSyntax: copierType,
                    activatorTypeSyntax: activatorType,
                    sourceLocation: type.SourceLocation));
            }

            foreach (var type in generatedInvokables)
            {
                registrations.Add(new SerializableMetadataRegistration(
                    sourceType: type.SourceType,
                    sortKey: type.TypeSyntax.ToString(),
                    serializerTypeSyntax: type.CodecTypeSyntax,
                    copierTypeSyntax: type.CopierTypeSyntax,
                    activatorTypeSyntax: null,
                    sourceLocation: type.SourceLocation));
            }

            return registrations
                .Select(registration =>
                {
                    return (
                        Registration: registration,
                        registration.SourceLocation.SourceOrderGroup,
                        registration.SourceLocation.FilePath,
                        registration.SourceLocation.Position);
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
            var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Length;
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
                copierTypeSyntax,
                proxy.SourceLocation);
        }

        private static string GetGeneratedInvokableClassName(ProxyInterfaceModel proxy, MethodModel method)
        {
            var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Length;
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
            return new CompoundTypeAliasModel(components.MoveToImmutable(), targetType);
        }

        private static TypeSyntax GetOpenTypeSyntax(TypeRef typeRef)
        {
            var syntax = typeRef.SyntaxString.Trim();
            if (syntax.StartsWith("global::", StringComparison.Ordinal))
            {
                syntax = syntax.Substring("global::".Length);
            }

            var typeSyntax = syntax switch
            {
                "bool" or "System.Boolean" => ParseName("bool"),
                "byte" or "System.Byte" => ParseName("byte"),
                "sbyte" or "System.SByte" => ParseName("sbyte"),
                "short" or "System.Int16" => ParseName("short"),
                "ushort" or "System.UInt16" => ParseName("ushort"),
                "int" or "System.Int32" => ParseName("int"),
                "uint" or "System.UInt32" => ParseName("uint"),
                "long" or "System.Int64" => ParseName("long"),
                "ulong" or "System.UInt64" => ParseName("ulong"),
                "float" or "System.Single" => ParseName("float"),
                "double" or "System.Double" => ParseName("double"),
                "decimal" or "System.Decimal" => ParseName("decimal"),
                "char" or "System.Char" => ParseName("char"),
                "string" or "System.String" => ParseName("string"),
                "object" or "System.Object" => ParseName("object"),
                _ => typeRef.ToTypeSyntax(),
            };

            return (TypeSyntax)OpenGenericTypeSyntaxRewriter.Instance.Visit(typeSyntax);
        }

        private static TypeOfExpressionSyntax CreateTypeOfExpression(TypeRef typeRef)
        {
            var result = TypeOfExpression(GetOpenTypeSyntax(typeRef));
            return IsPredefinedTypeRef(typeRef)
                ? result
                    .WithOpenParenToken(Token(TriviaList(Space), SyntaxKind.OpenParenToken, TriviaList(Space)))
                    .WithCloseParenToken(Token(TriviaList(Space), SyntaxKind.CloseParenToken, TriviaList(Space)))
                : result;
        }

        private static bool IsPredefinedTypeRef(TypeRef typeRef)
        {
            var syntax = typeRef.SyntaxString.Trim();
            if (syntax.StartsWith("global::", StringComparison.Ordinal))
            {
                syntax = syntax.Substring("global::".Length);
            }

            return syntax is "bool" or "System.Boolean"
                or "byte" or "System.Byte"
                or "sbyte" or "System.SByte"
                or "short" or "System.Int16"
                or "ushort" or "System.UInt16"
                or "int" or "System.Int32"
                or "uint" or "System.UInt32"
                or "long" or "System.Int64"
                or "ulong" or "System.UInt64"
                or "float" or "System.Single"
                or "double" or "System.Double"
                or "decimal" or "System.Decimal"
                or "char" or "System.Char"
                or "string" or "System.String"
                or "object" or "System.Object";
        }

        private sealed class OpenGenericTypeSyntaxRewriter : CSharpSyntaxRewriter
        {
            public static readonly OpenGenericTypeSyntaxRewriter Instance = new();

            public override SyntaxNode VisitGenericName(GenericNameSyntax node)
            {
                var visited = (GenericNameSyntax)base.VisitGenericName(node);
                var argumentCount = visited.TypeArgumentList.Arguments.Count;
                return visited.WithTypeArgumentList(TypeArgumentList(SeparatedList<TypeSyntax>(
                    Enumerable.Range(0, argumentCount).Select(static _ => OmittedTypeArgument()))));
            }
        }

        private TypeSyntax GetGeneratedProxyTypeSyntax(ProxyInterfaceModel proxy)
        {
            var genericArity = Math.Max(proxy.TypeParameters.Length, CountGenericArguments(proxy.InterfaceType));
            return CreateGeneratedTypeSyntax(proxy.GeneratedNamespace, ProxyGenerator.GetSimpleClassName(proxy.Name), genericArity);
        }

        private static int CountGenericArguments(TypeRef typeRef)
        {
            var typeSyntax = typeRef.ToTypeSyntax();
            var count = 0;
            foreach (var genericName in typeSyntax.DescendantNodesAndSelf().OfType<GenericNameSyntax>())
            {
                count += genericName.TypeArgumentList.Arguments.Count;
            }

            return count;
        }

        private static TypeSyntax TryGetDefaultCopierType(TypeRef originalType, ImmutableArray<DefaultCopierModel> defaultCopiers)
        {
            foreach (var copier in defaultCopiers)
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
                TypeSyntax copierTypeSyntax,
                SourceLocationModel sourceLocation)
            {
                TypeSyntax = typeSyntax;
                SourceType = sourceType;
                Aliases = aliases;
                CodecTypeSyntax = codecTypeSyntax;
                CopierTypeSyntax = copierTypeSyntax;
                SourceLocation = sourceLocation;
            }

            public TypeSyntax TypeSyntax { get; }
            public TypeRef SourceType { get; }
            public ImmutableArray<CompoundTypeAliasModel> Aliases { get; }
            public TypeSyntax CodecTypeSyntax { get; }
            public TypeSyntax CopierTypeSyntax { get; }
            public SourceLocationModel SourceLocation { get; }
        }

        private sealed class CompoundTypeAliasEmissionTree
        {
            private Dictionary<CompoundAliasComponentModel, CompoundTypeAliasEmissionTree> _children;
            private TypeRef _value;
            private bool _hasValue;

            private CompoundTypeAliasEmissionTree(CompoundAliasComponentModel key, TypeRef value, bool hasKey, bool hasValue)
            {
                Key = key;
                _value = value;
                HasKey = hasKey;
                _hasValue = hasValue;
            }

            public static CompoundTypeAliasEmissionTree Create() => new(default, TypeRef.Empty, hasKey: false, hasValue: false);

            public CompoundAliasComponentModel Key { get; }
            public bool HasKey { get; }
            public bool HasValue => _hasValue;
            public TypeRef Value => _value;
            public Dictionary<CompoundAliasComponentModel, CompoundTypeAliasEmissionTree> Children => _children;

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

            private CompoundTypeAliasEmissionTree GetChildOrDefault(CompoundAliasComponentModel key)
            {
                TryGetChild(key, out var result);
                return result;
            }

            private bool TryGetChild(CompoundAliasComponentModel key, out CompoundTypeAliasEmissionTree result)
            {
                if (_children is { } children)
                {
                    return children.TryGetValue(key, out result);
                }

                result = default;
                return false;
            }

            private CompoundTypeAliasEmissionTree AddInternal(CompoundAliasComponentModel key, TypeRef value, bool hasValue)
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

                var child = new CompoundTypeAliasEmissionTree(key, value, hasKey: true, hasValue: hasValue);
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
                TypeSyntax activatorTypeSyntax,
                SourceLocationModel sourceLocation)
            {
                SourceType = sourceType;
                SortKey = sortKey;
                SerializerTypeSyntax = serializerTypeSyntax;
                CopierTypeSyntax = copierTypeSyntax;
                ActivatorTypeSyntax = activatorTypeSyntax;
                SourceLocation = sourceLocation;
            }

            public TypeRef SourceType { get; }
            public string SortKey { get; }
            public TypeSyntax SerializerTypeSyntax { get; }
            public TypeSyntax CopierTypeSyntax { get; }
            public TypeSyntax ActivatorTypeSyntax { get; }
            public SourceLocationModel SourceLocation { get; }
        }
    }
}
