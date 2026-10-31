using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator;

internal class MetadataGenerator(
    MetadataAggregateModel metadataModel,
    string assemblyName,
    bool supportsModuleInitializers)
{
    private const string TypeManifestOptionsType = "global::Orleans.Serialization.Configuration.TypeManifestOptions";
    private const string TypeManifestProviderBaseType = "global::Orleans.Serialization.Configuration.TypeManifestProviderBase";
    private const string ProviderMetadataProviderType = "global::Orleans.Serialization.Configuration.IProviderMetadataProvider";
    private const string ProviderDictionaryType = "global::System.Collections.Generic.IDictionary<(string Target, string Kind, string Name), global::System.Type>";
    private const string SerializationType = "global::Orleans.Serialization.Configuration.SerializationType";

    private readonly MetadataAggregateModel _metadataModel = metadataModel;
    private readonly string _assemblyName = assemblyName ?? "Assembly";
    private readonly bool _supportsModuleInitializers = supportsModuleInitializers;

    /// <summary>The simple name of the generated metadata provider class.</summary>
    public string ClassName => "Metadata_" + SyntaxGeneration.Identifier.SanitizeIdentifierName(_assemblyName);

    public void WriteMetadata(SourceWriter writer)
    {
        var model = _metadataModel;
        var providers = model.ReferenceAssemblyData.RegisteredProviders
            .Select(static provider => $"providers[({CodeText.StringLiteral(provider.Target)}, {CodeText.StringLiteral(provider.Kind)}, {CodeText.StringLiteral(provider.Name)})] = typeof({CodeText.Type(provider.Type.ToTypeSyntax())});")
            .ToList();

        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        writer.WriteLine(providers.Count > 0
            ? $"internal sealed class {ClassName} : {TypeManifestProviderBaseType}, {ProviderMetadataProviderType}"
            : $"internal sealed class {ClassName} : {TypeManifestProviderBaseType}");
        writer.OpenBlock();
        writer.WriteLine($"protected override void ConfigureInner({TypeManifestOptionsType} config)");
        writer.OpenBlock();
        WriteConfigurationStatements(writer);
        writer.CloseBlock();

        if (providers.Count > 0)
        {
            writer.WriteLine($"public void ConfigureProviders({ProviderDictionaryType} providers)");
            writer.OpenBlock();
            foreach (var provider in providers)
            {
                writer.WriteLine(provider);
            }

            writer.CloseBlock();

            if (_supportsModuleInitializers)
            {
                writer.WriteLine("[global::System.Runtime.CompilerServices.ModuleInitializerAttribute]");
                writer.WriteLine("internal static void RegisterProviders()");
                writer.OpenBlock();
                writer.WriteLine($"global::Orleans.Serialization.Configuration.ProviderMetadataRegistry.Register(new {ClassName}());");
                writer.CloseBlock();
            }
        }

        writer.CloseBlock();
    }

    private void WriteConfigurationStatements(SourceWriter writer)
    {
        var model = _metadataModel;
        var orderedProxyInterfaces = GetOrderedProxyInterfaces(model.ProxyInterfaces);
        var generatedInvokableActivatorMetadataNames = new HashSet<string>(
            model.GeneratedInvokableActivatorMetadataNames,
            StringComparer.Ordinal);
        var generatedInvokableMetadataNames = new HashSet<string>(
            model.GeneratedInvokableMetadataNames,
            StringComparer.Ordinal);
        var generatedInvokables = GetGeneratedInvokableMetadata(
            orderedProxyInterfaces,
            generatedInvokableMetadataNames,
            generatedInvokableActivatorMetadataNames);
        var serializableRegistrations = GetOrderedSerializableRegistrations(model, generatedInvokables);

        foreach (var registration in serializableRegistrations)
        {
            var sourceType = OpenType(registration.SourceType);
            var serializer = CodeText.Type(registration.SerializerTypeSyntax);
            WriteRegistration(writer, "AddSerializer", serializer, sourceType);
            if (registration.BaseCodec)
            {
                WriteRegistration(writer, "AddBaseCodec", serializer, sourceType);
            }

            if (registration.ValueSerializer)
            {
                WriteRegistration(writer, "AddValueSerializer", serializer, sourceType);
            }
        }

        foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Serializer))
        {
            WriteContracts(writer, type, "AddSerializer");
        }

        foreach (var registration in serializableRegistrations)
        {
            if (registration.CopierTypeSyntax is not null)
            {
                var sourceType = OpenType(registration.SourceType);
                var copier = CodeText.Type(registration.CopierTypeSyntax);
                WriteRegistration(writer, "AddCopier", copier, sourceType);
                if (registration.BaseCopier)
                {
                    WriteRegistration(writer, "AddBaseCopier", copier, sourceType);
                }
            }
        }

        foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Copier))
        {
            WriteContracts(writer, type, "AddCopier");
        }

        foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Converter))
        {
            WriteContracts(writer, type, "AddConverter");
        }

        foreach (var type in orderedProxyInterfaces)
        {
            WriteRegistration(writer, "AddInterfaceProxy", CodeText.Type(GetGeneratedProxyTypeSyntax(type)));
        }

        foreach (var type in orderedProxyInterfaces.Select(static proxy => proxy.InterfaceType).Distinct())
        {
            WriteRegistration(writer, "AddInterface", OpenType(type));
        }

        foreach (var type in GetOrderedInterfaceImplementations(model.InterfaceImplementations))
        {
            WriteRegistration(writer, "AddInterfaceImplementation", OpenType(type.ImplementationType));
        }

        foreach (var registration in serializableRegistrations)
        {
            if (registration.ActivatorTypeSyntax is not null)
            {
                WriteRegistration(writer, "AddActivator", CodeText.Type(registration.ActivatorTypeSyntax), OpenType(registration.SourceType));
            }
        }

        foreach (var type in model.RegisteredCodecs.Where(static codec => codec.Kind == RegisteredCodecKind.Activator))
        {
            WriteContracts(writer, type, "AddActivator");
        }

        foreach (var type in model.ReferenceAssemblyData.WellKnownTypeIds)
        {
            WriteTypeMetadataRegistration(writer, "config.WellKnownTypeIds", CodeText.Literal(type.Id), $"typeof({OpenType(type.Type)})");
        }

        foreach (var type in model.ReferenceAssemblyData.TypeAliases)
        {
            WriteTypeMetadataRegistration(writer, "config.WellKnownTypeAliases", CodeText.StringLiteral(type.Alias), $"typeof({OpenType(type.Type)})");
        }

        WriteCompoundTypeAliases(writer, generatedInvokables);
    }

    private void WriteCompoundTypeAliases(SourceWriter writer, ImmutableArray<GeneratedInvokableMetadata> generatedInvokables)
    {
        var aliases = _metadataModel.ReferenceAssemblyData.CompoundTypeAliases
            .OrderBy(static entry => entry.Components.Length)
            .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        var generatedAliases = generatedInvokables
            .SelectMany(static invokable => invokable.Aliases)
            .ToImmutableArray();

        var aliasTree = CompoundTypeAliasEmissionTree.Create();
        foreach (var alias in aliases.Concat(generatedAliases).Concat(_metadataModel.GeneratedCompoundTypeAliases))
        {
            aliasTree.Add(alias.Components, alias.TargetType);
        }

        var nodeId = 0;
        WriteCompoundTypeAliases(writer, "config.CompoundTypeAliases", aliasTree, ref nodeId);
    }

    private static void WriteCompoundTypeAliases(
        SourceWriter writer,
        string tree,
        CompoundTypeAliasEmissionTree aliases,
        ref int nodeId)
    {
        string node;
        if (!aliases.HasKey)
        {
            node = tree;
        }
        else
        {
            // Every keyed node consumes an identifier, including leaves which declare no local.
            node = $"n{++nodeId}";
            var invocation = new StringBuilder();
            invocation.Append(tree).Append(aliases.HasValue ? ".Add(" : ".GetOrAdd(").Append(CreateCompoundAliasArgument(aliases.Key));
            if (aliases.HasValue)
            {
                invocation.Append(", typeof(").Append(OpenType(aliases.Value)).Append(')');
            }

            invocation.Append(')');
            writer.WriteLine(aliases.Children is { Count: > 0 } ? $"var {node} = {invocation};" : $"{invocation};");
        }

        if (aliases.Children is { Count: > 0 })
        {
            foreach (var child in aliases.Children.Values)
            {
                WriteCompoundTypeAliases(writer, node, child, ref nodeId);
            }
        }
    }

    private static string CreateCompoundAliasArgument(CompoundAliasComponentModel component)
        => component.IsType
            ? $"typeof({OpenType(component.TypeValue)})"
            : CodeText.StringLiteral(component.StringValue!);

    private static ImmutableArray<ProxyInterfaceModel> GetOrderedProxyInterfaces(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        => OrderBySourceLocation(proxyInterfaces, static proxy => proxy.SourceLocation, static proxy => proxy.InterfaceType.SyntaxString);

    private static ImmutableArray<InterfaceImplementationModel> GetOrderedInterfaceImplementations(ImmutableArray<InterfaceImplementationModel> interfaceImplementations)
        => OrderBySourceLocation(interfaceImplementations, static implementation => implementation.SourceLocation, static implementation => implementation.ImplementationType.SyntaxString);

    private static ImmutableArray<T> OrderBySourceLocation<T>(
        ImmutableArray<T> entries,
        Func<T, SourceLocationModel> locationSelector,
        Func<T, string> sortKeySelector)
    {
        return [.. entries
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
            .Select(static entry => entry.Entry)];
    }

    private ImmutableArray<GeneratedInvokableMetadata> GetGeneratedInvokableMetadata(
        ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
        HashSet<string> generatedInvokableMetadataNames,
        HashSet<string> generatedInvokableActivatorMetadataNames)
    {
        var result = ImmutableArray.CreateBuilder<GeneratedInvokableMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var proxy in proxyInterfaces)
        {
            var explicitlyClaimedMethodIds = new HashSet<(TypeRef InterfaceType, TypeRef OriginalInterfaceType, string MethodId)>(
                proxy.Methods.SelectMany(method => GetExplicitlyClaimedMethodIds(proxy, method)));
            foreach (var method in proxy.Methods)
            {
                var metadata = CreateGeneratedInvokableMetadata(
                    proxy,
                    method,
                    generatedInvokableActivatorMetadataNames,
                    explicitlyClaimedMethodIds);
                var key = metadata.TypeSyntax.ToString();
                if (generatedInvokableMetadataNames.Contains(metadata.MetadataName) && seen.Add(key))
                {
                    result.Add(metadata);
                }
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<SerializableMetadataRegistration> GetOrderedSerializableRegistrations(
        MetadataAggregateModel model,
        ImmutableArray<GeneratedInvokableMetadata> generatedInvokables)
    {
        var registrations = new List<SerializableMetadataRegistration>(model.SerializableTypes.Length + generatedInvokables.Length);

        foreach (var type in model.SerializableTypes)
        {
            TypeSyntax? copierType = null;
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
                sourceLocation: type.SourceLocation,
                baseCodec: !type.IsValueType && !type.IsSealedType,
                valueSerializer: type.IsValueType && !type.IsEnumType,
                baseCopier: !type.IsShallowCopyable && (!type.IsSealedType
                    || type.IsExceptionType && type.SerializationHooks.IsEmpty)));
        }

        foreach (var type in generatedInvokables)
        {
            registrations.Add(new SerializableMetadataRegistration(
                sourceType: new TypeRef(type.TypeSyntax.ToString()),
                sortKey: type.TypeSyntax.ToString(),
                serializerTypeSyntax: type.CodecTypeSyntax,
                copierTypeSyntax: type.CopierTypeSyntax,
                activatorTypeSyntax: type.ActivatorTypeSyntax,
                sourceLocation: type.SourceLocation));
        }

        return [.. registrations
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
            .Select(static entry => entry.Registration)];
    }

    private static GeneratedInvokableMetadata CreateGeneratedInvokableMetadata(
        ProxyInterfaceModel proxy,
        MethodModel method,
        HashSet<string> generatedInvokableActivatorMetadataNames,
        HashSet<(TypeRef InterfaceType, TypeRef OriginalInterfaceType, string MethodId)> explicitlyClaimedMethodIds)
    {
        var generatedNamespace = method.ContainingInterfaceGeneratedNamespace;
        var name = GetGeneratedInvokableClassName(proxy, method);
        var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Length;
        var typeSyntax = CreateGeneratedTypeSyntax(generatedNamespace, name, genericArity);
        var targetType = new TypeRef(typeSyntax.ToString());
        var aliases = CreateGeneratedInvokableAliases(proxy, method, targetType, explicitlyClaimedMethodIds);
        var codecTypeSyntax = GetCodecTypeName(generatedNamespace, name, genericArity);
        var copierTypeSyntax = GetCopierTypeName(generatedNamespace, name, genericArity);
        var metadataName = GetGeneratedInvokableMetadataName(generatedNamespace, name, genericArity);
        var activatorTypeSyntax = generatedInvokableActivatorMetadataNames.Contains(metadataName)
            ? GetActivatorTypeName(generatedNamespace, name, genericArity)
            : null;

        return new GeneratedInvokableMetadata(
            metadataName,
            typeSyntax,
            method.ContainingInterfaceType,
            aliases,
            codecTypeSyntax,
            copierTypeSyntax,
            activatorTypeSyntax,
            proxy.SourceLocation);
    }

    private static string GetGeneratedInvokableMetadataName(string generatedNamespace, string name, int genericArity)
        => genericArity == 0
            ? $"{generatedNamespace}.{name}"
            : $"{generatedNamespace}.{name}`{genericArity}";

    private static string GetGeneratedInvokableClassName(ProxyInterfaceModel proxy, MethodModel method)
    {
        var genericArity = method.ContainingInterfaceTypeParameterCount + method.TypeParameters.Length;
        var typeArgs = genericArity > 0 ? "_" + genericArity : string.Empty;
        return $"Invokable_{method.ContainingInterfaceName}_{proxy.ProxyBase.GeneratedClassNameComponent}_{method.GeneratedMethodId}{typeArgs}";
    }

    private static ImmutableArray<CompoundTypeAliasModel> CreateGeneratedInvokableAliases(
        ProxyInterfaceModel proxy,
        MethodModel method,
        TypeRef targetType,
        HashSet<(TypeRef InterfaceType, TypeRef OriginalInterfaceType, string MethodId)> explicitlyClaimedMethodIds)
    {
        var result = ImmutableArray.CreateBuilder<CompoundTypeAliasModel>(3);
        if (!string.Equals(method.MethodId, method.GeneratedMethodId, StringComparison.Ordinal))
        {
            result.Add(CreateGeneratedInvokableAlias(proxy, method, method.MethodId, targetType));
        }

        if (method.ClaimedGeneratedMethodId is { } claimedGeneratedMethodId
            && !string.Equals(claimedGeneratedMethodId, method.MethodId, StringComparison.Ordinal)
            && !string.Equals(claimedGeneratedMethodId, method.GeneratedMethodId, StringComparison.Ordinal))
        {
            result.Add(CreateGeneratedInvokableAlias(proxy, method, claimedGeneratedMethodId, targetType));
        }

        if (!explicitlyClaimedMethodIds.Contains(GetInvokableAliasKey(proxy, method, method.GeneratedMethodId)))
        {
            result.Add(CreateGeneratedInvokableAlias(proxy, method, method.GeneratedMethodId, targetType));
        }
        return result.ToImmutable();
    }

    private static IEnumerable<(TypeRef InterfaceType, TypeRef OriginalInterfaceType, string MethodId)> GetExplicitlyClaimedMethodIds(
        ProxyInterfaceModel proxy,
        MethodModel method)
    {
        if (method.HasAlias)
        {
            yield return GetInvokableAliasKey(proxy, method, method.MethodId);
        }

        if (method.ClaimedGeneratedMethodId is { } claimedGeneratedMethodId)
        {
            yield return GetInvokableAliasKey(proxy, method, claimedGeneratedMethodId);
        }
    }

    private static (TypeRef InterfaceType, TypeRef OriginalInterfaceType, string MethodId) GetInvokableAliasKey(
        ProxyInterfaceModel proxy,
        MethodModel method,
        string methodId) =>
        (
            method.ContainingInterfaceType,
            proxy.ProxyBase.IsExtension ? method.OriginalContainingInterfaceType : default,
            methodId);

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
            "bool" or "System.Boolean" => ParseTypeName("bool"),
            "byte" or "System.Byte" => ParseTypeName("byte"),
            "sbyte" or "System.SByte" => ParseTypeName("sbyte"),
            "short" or "System.Int16" => ParseTypeName("short"),
            "ushort" or "System.UInt16" => ParseTypeName("ushort"),
            "int" or "System.Int32" => ParseTypeName("int"),
            "uint" or "System.UInt32" => ParseTypeName("uint"),
            "long" or "System.Int64" => ParseTypeName("long"),
            "ulong" or "System.UInt64" => ParseTypeName("ulong"),
            "float" or "System.Single" => ParseTypeName("float"),
            "double" or "System.Double" => ParseTypeName("double"),
            "decimal" or "System.Decimal" => ParseTypeName("decimal"),
            "char" or "System.Char" => ParseTypeName("char"),
            "string" or "System.String" => ParseTypeName("string"),
            "object" or "System.Object" => ParseTypeName("object"),
            _ => typeRef.ToTypeSyntax(),
        };

        return (TypeSyntax)OpenGenericTypeSyntaxRewriter.Instance.Visit(typeSyntax);
    }

    private static string OpenType(TypeRef typeRef) => CodeText.Type(GetOpenTypeSyntax(typeRef));

    private sealed class OpenGenericTypeSyntaxRewriter : CSharpSyntaxRewriter
    {
        public static readonly OpenGenericTypeSyntaxRewriter Instance = new();

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
            var argumentCount = visited.TypeArgumentList.Arguments.Count;
            return visited.WithTypeArgumentList(TypeArgumentList(SeparatedList<TypeSyntax>(
                Enumerable.Range(0, argumentCount).Select(static _ => OmittedTypeArgument()))));
        }
    }

    private static TypeSyntax GetGeneratedProxyTypeSyntax(ProxyInterfaceModel proxy)
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

    private static TypeSyntax? TryGetDefaultCopierType(TypeRef originalType, ImmutableArray<DefaultCopierModel> defaultCopiers)
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

    private static void WriteRegistration(SourceWriter writer, string method, string type)
        => writer.WriteLine($"config.{method}(typeof({type}));");

    private static void WriteRegistration(SourceWriter writer, string method, string type, string target)
        => writer.WriteLine($"config.{method}(typeof({type}), typeof({target}));");

    private static void WriteContracts(
        SourceWriter writer,
        RegisteredCodecModel implementation,
        string legacyMethod)
    {
        var implementationType = OpenType(implementation.Type);
        if (implementation.Contracts.IsEmpty)
        {
            WriteRegistration(writer, legacyMethod, implementationType);
            return;
        }

        foreach (var contract in implementation.Contracts)
        {
            var arguments = new StringBuilder();
            arguments.Append("typeof(").Append(implementationType).Append(')');
            if (contract.TargetDescription is { } targetDescription)
            {
                var contractType = contract.RegistrationMethod switch
                {
                    "AddSerializer" => "global::Orleans.Serialization.Codecs.IFieldCodec<>",
                    "AddBaseCodec" => "global::Orleans.Serialization.Serializers.IBaseCodec<>",
                    "AddValueSerializer" => "global::Orleans.Serialization.Serializers.IValueSerializer<>",
                    "AddCopier" => "global::Orleans.Serialization.Cloning.IDeepCopier<>",
                    "AddBaseCopier" => "global::Orleans.Serialization.Cloning.IBaseCopier<>",
                    "AddActivator" => "global::Orleans.Serialization.Activators.IActivator<>",
                    _ => "global::Orleans.IConverter<, >"
                };
                arguments.Append(", typeof(").Append(contractType).Append("), ").Append(GetSerializationTypeExpression(targetDescription));
                if (contract.SurrogateDescription is { } describedSurrogate)
                {
                    arguments.Append(", ").Append(GetSerializationTypeExpression(describedSurrogate));
                }
                else if (contract.Surrogate is { } concreteSurrogate)
                {
                    arguments.Append(", ").Append(SerializationType).Append(".Create(").Append(GetTargetExpression(concreteSurrogate)).Append(')');
                }

                writer.WriteLine($"config.AddSerializationContract({arguments});");
                continue;
            }

            arguments.Append(", ").Append(GetTargetExpression(contract.Target));
            if (contract.SurrogateDescription is { } description)
            {
                arguments.Append(", ").Append(GetSerializationTypeExpression(description));
            }
            else if (contract.Surrogate is { } surrogate)
            {
                arguments.Append(", ").Append(GetTargetExpression(surrogate));
            }

            writer.WriteLine($"config.{CodeText.Identifier(contract.RegistrationMethod)}({arguments});");
        }
    }

    private static string GetTargetExpression(ContractTargetModel target)
        => target.IsAccessible
            ? $"typeof({CodeText.Type(target.Type.ToTypeSyntax())})"
            : $"global::System.Type.GetType({CodeText.StringLiteral(target.RuntimeTypeName)}, true)!";

    private static string GetSerializationTypeExpression(SerializationTypeModel description)
    {
        if (description.ParameterIndex >= 0)
        {
            return $"{SerializationType}.Parameter({CodeText.Literal(description.ParameterIndex)})";
        }

        if (description.ArrayRank > 0)
        {
            return $"{SerializationType}.Array({GetSerializationTypeExpression(description.Arguments[0])}, {CodeText.Literal(description.ArrayRank)})";
        }

        var arguments = new StringBuilder(GetTargetExpression(description.Type!.Value));
        foreach (var argument in description.Arguments)
        {
            arguments.Append(", ").Append(GetSerializationTypeExpression(argument));
        }

        return $"{SerializationType}.Create({arguments})";
    }

    /// <summary>
    /// Writes a block which registers <paramref name="type"/> for <paramref name="key"/> in <paramref name="dictionary"/>,
    /// failing when another type is already registered for the key.
    /// </summary>
    internal static void WriteTypeMetadataRegistration(SourceWriter writer, string dictionary, string key, string type)
    {
        writer.OpenBlock();
        writer.WriteLine($"var registeredType = {type};");
        writer.WriteLine($"if ({dictionary}.TryGetValue({key}, out var existingType) && existingType != registeredType)");
        writer.OpenBlock();
        writer.WriteLine($"throw new global::System.InvalidOperationException(\"Conflicting type metadata registration for \" + {key} + \".\");");
        writer.CloseBlock();
        writer.WriteLine($"{dictionary}[{key}] = registeredType;");
        writer.CloseBlock();
    }

    private static bool ShouldGenerateActivator(SerializableTypeModel type)
        => !type.IsAbstractType
            && !type.IsEnumType
            && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator || type.HasActivatorConstructor);

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

    private readonly struct GeneratedInvokableMetadata(
        string metadataName,
        TypeSyntax typeSyntax,
        TypeRef sourceType,
        ImmutableArray<CompoundTypeAliasModel> aliases,
        TypeSyntax codecTypeSyntax,
        TypeSyntax? copierTypeSyntax,
        TypeSyntax? activatorTypeSyntax,
        SourceLocationModel sourceLocation)
    {
        public string MetadataName { get; } = metadataName;
        public TypeSyntax TypeSyntax { get; } = typeSyntax;
        public TypeRef SourceType { get; } = sourceType;
        public ImmutableArray<CompoundTypeAliasModel> Aliases { get; } = aliases;
        public TypeSyntax CodecTypeSyntax { get; } = codecTypeSyntax;
        public TypeSyntax? CopierTypeSyntax { get; } = copierTypeSyntax;
        public TypeSyntax? ActivatorTypeSyntax { get; } = activatorTypeSyntax;
        public SourceLocationModel SourceLocation { get; } = sourceLocation;
    }

    private sealed class CompoundTypeAliasEmissionTree
    {
        private CompoundTypeAliasEmissionTree(CompoundAliasComponentModel key, TypeRef value, bool hasKey, bool hasValue)
        {
            Key = key;
            Value = value;
            HasKey = hasKey;
            HasValue = hasValue;
        }

        public static CompoundTypeAliasEmissionTree Create() => new(default, TypeRef.Empty, hasKey: false, hasValue: false);

        public CompoundAliasComponentModel Key { get; }
        public bool HasKey { get; }
        public bool HasValue { get; private set; }
        public TypeRef Value { get; private set; }
        public Dictionary<CompoundAliasComponentModel, CompoundTypeAliasEmissionTree>? Children { get; private set; }

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

        private CompoundTypeAliasEmissionTree? GetChildOrDefault(CompoundAliasComponentModel key)
        {
            TryGetChild(key, out var result);
            return result;
        }

        private bool TryGetChild(CompoundAliasComponentModel key, out CompoundTypeAliasEmissionTree? result)
        {
            if (Children is { } children)
            {
                return children.TryGetValue(key, out result);
            }

            result = default;
            return false;
        }

        private CompoundTypeAliasEmissionTree AddInternal(CompoundAliasComponentModel key, TypeRef value, bool hasValue)
        {
            Children ??= new();

            if (Children.TryGetValue(key, out var existing))
            {
                if (hasValue)
                {
                    if (existing.HasValue && !existing.Value.Equals(value))
                    {
                        throw new ArgumentException($"A key with the value '{key}' already exists.");
                    }

                    existing.Value = value;
                    existing.HasValue = true;
                }

                return existing;
            }

            var child = new CompoundTypeAliasEmissionTree(key, value, hasKey: true, hasValue: hasValue);
            Children.Add(key, child);
            return child;
        }
    }

    private readonly struct SerializableMetadataRegistration(
        TypeRef sourceType,
        string sortKey,
        TypeSyntax serializerTypeSyntax,
        TypeSyntax? copierTypeSyntax,
        TypeSyntax? activatorTypeSyntax,
        SourceLocationModel sourceLocation,
        bool baseCodec = false,
        bool valueSerializer = false,
        bool baseCopier = false)
    {
        public TypeRef SourceType { get; } = sourceType;
        public string SortKey { get; } = sortKey;
        public TypeSyntax SerializerTypeSyntax { get; } = serializerTypeSyntax;
        public TypeSyntax? CopierTypeSyntax { get; } = copierTypeSyntax;
        public TypeSyntax? ActivatorTypeSyntax { get; } = activatorTypeSyntax;
        public SourceLocationModel SourceLocation { get; } = sourceLocation;
        public bool BaseCodec { get; } = baseCodec;
        public bool ValueSerializer { get; } = valueSerializer;
        public bool BaseCopier { get; } = baseCopier;
    }
}
