using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal static class MetadataAggregateModelBuilder
{
    /// <summary>
    /// Creates a <see cref="MetadataAggregateModel"/> from the collected pipeline outputs.
    /// This provides a single equality checkpoint so that downstream generation can be
    /// skipped when no upstream pipeline has changed.
    /// </summary>
    internal static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
        ReferenceAssemblyModel refData)
        => CreateMetadataAggregate(
            assemblyName,
            serializableTypes,
            proxyInterfaces.IsDefaultOrEmpty
                ? []
                : proxyInterfaces.Select(static proxy => new ProxyOutputModel(proxy, EquatableArray<string>.Empty, useDeclaredInvokableFallback: false)).ToImmutableArray(),
            refData);

    internal static MetadataAggregateModel CreateMetadataAggregate(
        string assemblyName,
        ImmutableArray<SerializableTypeModel> serializableTypes,
        ImmutableArray<ProxyOutputModel> proxyOutputs,
        ReferenceAssemblyModel refData)
    {
        var normalizedReferenceData = NormalizeReferenceAssemblyData(refData);
        var normalizedSerializableTypes = MergeSerializableTypes(serializableTypes, normalizedReferenceData.ReferencedSerializableTypes);
        var sourceProxyInterfaces = proxyOutputs.IsDefaultOrEmpty
            ? []
            : proxyOutputs.Select(static output => output.ProxyInterface).ToImmutableArray();
        var normalizedProxyInterfaces = MergeProxyInterfaces(sourceProxyInterfaces, normalizedReferenceData.ReferencedProxyInterfaces);
        var activatableTypes = GetActivatableTypes(normalizedSerializableTypes);
        var generatedProxyTypes = GetGeneratedProxyTypes(normalizedProxyInterfaces);
        var invokableInterfaces = GetInvokableInterfaces(normalizedProxyInterfaces);
        var generatedInvokableActivatorMetadataNames = GetGeneratedInvokableActivatorMetadataNames(proxyOutputs);
        var defaultCopiers = GetDefaultCopiers(normalizedSerializableTypes);

        return new MetadataAggregateModel(
            AssemblyName: assemblyName,
            SerializableTypes: normalizedSerializableTypes,
            ProxyInterfaces: normalizedProxyInterfaces,
            RegisteredCodecs: normalizedReferenceData.RegisteredCodecs,
            ReferenceAssemblyData: normalizedReferenceData,
            ActivatableTypes: activatableTypes,
            GeneratedProxyTypes: generatedProxyTypes,
            InvokableInterfaces: invokableInterfaces,
            GeneratedInvokableActivatorMetadataNames: generatedInvokableActivatorMetadataNames,
            InterfaceImplementations: normalizedReferenceData.InterfaceImplementations,
            DefaultCopiers: defaultCopiers);
    }

    private static ReferenceAssemblyModel NormalizeReferenceAssemblyData(ReferenceAssemblyModel referenceData)
    {
        var applicationParts = referenceData.ApplicationParts
            .Distinct()
            .ToImmutableArray();

        var wellKnownTypeIds = referenceData.WellKnownTypeIds
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id)
            .ToImmutableArray();

        var typeAliases = referenceData.TypeAliases
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Alias, StringComparer.Ordinal)
            .ToImmutableArray();

        var compoundTypeAliases = referenceData.CompoundTypeAliases
            .Distinct()
            .OrderBy(static entry => ReferenceAssemblyModelExtractor.GetCompoundTypeAliasOrderKey(entry), StringComparer.Ordinal)
            .ThenBy(static entry => entry.TargetType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        var referencedSerializableTypes = DeduplicateSerializableTypes(referenceData.ReferencedSerializableTypes);

        var referencedProxyInterfaces = DeduplicateProxyInterfaces(referenceData.ReferencedProxyInterfaces);

        var registeredCodecs = referenceData.RegisteredCodecs
            .Distinct()
            .OrderBy(static entry => entry.Type.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Kind)
            .ToImmutableArray();

        var interfaceImplementations = referenceData.InterfaceImplementations
            .Distinct()
            .OrderBy(static entry => entry.ImplementationType.SyntaxString, StringComparer.Ordinal)
            .ToImmutableArray();

        return new ReferenceAssemblyModel(
            AssemblyName: referenceData.AssemblyName ?? string.Empty,
            ApplicationParts: applicationParts,
            WellKnownTypeIds: wellKnownTypeIds,
            TypeAliases: typeAliases,
            CompoundTypeAliases: compoundTypeAliases,
            ReferencedSerializableTypes: referencedSerializableTypes,
            ReferencedProxyInterfaces: referencedProxyInterfaces,
            RegisteredCodecs: registeredCodecs,
            InterfaceImplementations: interfaceImplementations);
    }

    internal static ImmutableArray<SerializableTypeModel> MergeSerializableTypes(
        ImmutableArray<SerializableTypeModel> source,
        ImmutableArray<SerializableTypeModel> referenced)
    {
        var merged = source.IsDefault ? [] : source;
        if (!referenced.IsDefaultOrEmpty)
        {
            merged = merged.AddRange(referenced);
        }

        return DeduplicateSerializableTypes(merged);
    }

    internal static ImmutableArray<ProxyInterfaceModel> MergeProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> source,
        ImmutableArray<ProxyInterfaceModel> referenced)
    {
        var merged = source.IsDefault ? [] : source;
        if (!referenced.IsDefaultOrEmpty)
        {
            merged = merged.AddRange(referenced);
        }

        return DeduplicateProxyInterfaces(merged);
    }

    internal static ImmutableArray<SerializableTypeModel> DeduplicateSerializableTypes(
        ImmutableArray<SerializableTypeModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        var selected = new Dictionary<string, SerializableTypeModel>(StringComparer.Ordinal);
        foreach (var entry in entries
            .Where(static entry => entry is not null)
            .OrderBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.TypeSyntax.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            var key = CreateTypeDedupeKey(
                entry.MetadataIdentity,
                entry.TypeSyntax.SyntaxString,
                entry.GeneratedNamespace,
                entry.Name);
            if (!selected.ContainsKey(key))
            {
                selected.Add(key, entry);
            }
        }

        return [.. OrderSerializableTypeModels(selected.Values)];
    }

    internal static ImmutableArray<ProxyInterfaceModel> DeduplicateProxyInterfaces(
        ImmutableArray<ProxyInterfaceModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        var selected = new Dictionary<string, ProxyInterfaceModel>(StringComparer.Ordinal);
        foreach (var entry in entries
            .Where(static entry => entry is not null)
            .OrderBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.InterfaceType.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            var key = CreateTypeDedupeKey(
                entry.MetadataIdentity,
                entry.InterfaceType.SyntaxString,
                entry.GeneratedNamespace,
                entry.Name);
            if (!selected.ContainsKey(key))
            {
                selected.Add(key, entry);
            }
        }

        return [.. OrderProxyInterfaceModels(selected.Values)];
    }

    internal static ImmutableArray<SerializableTypeModel> NormalizeSerializableTypeModels(
        ImmutableArray<SerializableTypeModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. OrderSerializableTypeModels(entries.Where(static entry => entry is not null))];
    }

    internal static ImmutableArray<ProxyInterfaceModel> NormalizeProxyInterfaceModels(
        ImmutableArray<ProxyInterfaceModel> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. OrderProxyInterfaceModels(entries.Where(static entry => entry is not null))];
    }

    private static string CreateTypeDedupeKey(
        TypeMetadataIdentity metadataIdentity,
        string typeSyntax,
        string generatedNamespace,
        string name)
    {
        if (!metadataIdentity.IsEmpty)
        {
            return string.Join(
                "|",
                "M",
                metadataIdentity.AssemblyIdentity ?? string.Empty,
                metadataIdentity.AssemblyName ?? string.Empty,
                metadataIdentity.MetadataName ?? string.Empty);
        }

        return string.Join(
            "|",
            "S",
            typeSyntax ?? string.Empty,
            generatedNamespace ?? string.Empty,
            name ?? string.Empty);
    }

    internal static IOrderedEnumerable<SerializableTypeModel> OrderSerializableTypeModels(IEnumerable<SerializableTypeModel> entries)
        => entries
            .OrderBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.TypeSyntax.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal);

    internal static IOrderedEnumerable<ProxyInterfaceModel> OrderProxyInterfaceModels(IEnumerable<ProxyInterfaceModel> entries)
        => entries
            .OrderBy(static entry => entry.MetadataIdentity.MetadataName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
            .ThenBy(static entry => entry.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static entry => entry.InterfaceType.SyntaxString, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.SourceOrderGroup)
            .ThenBy(static entry => entry.SourceLocation.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceLocation.Position)
            .ThenBy(static entry => entry.GeneratedNamespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal);

    private static ImmutableArray<TypeRef> GetActivatableTypes(ImmutableArray<SerializableTypeModel> serializableTypes)
        => [.. serializableTypes
            .Where(static type => ShouldGenerateActivator(type))
            .Select(static type => type.TypeSyntax)
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<TypeRef> GetGeneratedProxyTypes(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        => [.. proxyInterfaces
            .Select(static proxy => CreateGeneratedTypeRef(
                proxy.GeneratedNamespace,
                ProxyGenerator.GetSimpleClassName(proxy.Name),
                proxy.TypeParameters.Length))
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<TypeRef> GetInvokableInterfaces(ImmutableArray<ProxyInterfaceModel> proxyInterfaces)
        => [.. proxyInterfaces
            .Select(static proxy => proxy.InterfaceType)
            .Distinct()
            .OrderBy(static type => type.SyntaxString, StringComparer.Ordinal)];

    private static ImmutableArray<string> GetGeneratedInvokableActivatorMetadataNames(ImmutableArray<ProxyOutputModel> proxyOutputs)
    {
        if (proxyOutputs.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. proxyOutputs
            .SelectMany(static output => output.OwnedInvokableActivatorMetadataNames)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static metadataName => metadataName, StringComparer.Ordinal)];
    }

    private static ImmutableArray<DefaultCopierModel> GetDefaultCopiers(ImmutableArray<SerializableTypeModel> serializableTypes)
        => [.. serializableTypes
            .Where(static type => type.IsShallowCopyable && !type.IsGenericType)
            .Select(static type => new DefaultCopierModel(
                type.TypeSyntax,
                new TypeRef($"global::Orleans.Serialization.Cloning.ShallowCopier<{type.TypeSyntax.SyntaxString}>")))
            .Distinct()
            .OrderBy(static entry => entry.OriginalType.SyntaxString, StringComparer.Ordinal)];

    private static bool ShouldGenerateActivator(SerializableTypeModel type)
    {
        return !type.IsAbstractType
            && !type.IsEnumType
            && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator || type.HasActivatorConstructor);
    }

    private static TypeRef CreateGeneratedTypeRef(string generatedNamespace, string simpleName, int genericArity)
    {
        var qualifiedName = string.IsNullOrWhiteSpace(generatedNamespace)
            ? simpleName
            : $"{generatedNamespace}.{simpleName}";

        return genericArity > 0
            ? new TypeRef($"{qualifiedName}<{new string(',', genericArity - 1)}>")
            : new TypeRef(qualifiedName);
    }
}


