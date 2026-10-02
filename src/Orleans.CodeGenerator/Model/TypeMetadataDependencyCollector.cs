using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

internal static class TypeMetadataDependencyCollector
{
    private static readonly ConditionalWeakTable<Compilation, CollectionCache> Cache = new();

    public static EquatableArray<TypeMetadataIdentity> Collect(Compilation compilation, INamedTypeSymbol type, bool includeType = false)
        => Cache.GetValue(compilation, static _ => new CollectionCache()).Collect(type, includeType);

    private static EquatableArray<TypeMetadataIdentity> CollectCore(INamedTypeSymbol type, bool includeType)
    {
        var result = new HashSet<TypeMetadataIdentity>();
        if (includeType)
        {
            Visit(type);
        }

        foreach (var implementedInterface in type.AllInterfaces)
        {
            foreach (var argument in implementedInterface.TypeArguments)
            {
                Visit(argument);
            }
        }

        return result.OrderBy(static type => type.AssemblyName, StringComparer.Ordinal)
            .ThenBy(static type => type.MetadataName, StringComparer.Ordinal).ToImmutableArray();

        void Visit(ITypeSymbol symbol)
        {
            if (symbol is IArrayTypeSymbol array)
            {
                Visit(array.ElementType);
                return;
            }

            if (symbol is not INamedTypeSymbol named)
            {
                return;
            }

            foreach (var argument in named.TypeArguments)
            {
                Visit(argument);
            }

            if (!result.Add(TypeMetadataIdentity.Create(named)))
            {
                return;
            }

            if (named.ContainingType is { } containing)
            {
                Visit(containing);
            }

            foreach (var implementedInterface in named.OriginalDefinition.AllInterfaces)
            {
                foreach (var argument in implementedInterface.TypeArguments)
                {
                    Visit(argument);
                }
            }
        }
    }

    private sealed class CollectionCache
    {
        private readonly ConcurrentDictionary<INamedTypeSymbol, Lazy<EquatableArray<TypeMetadataIdentity>>> _interfaceArguments =
            new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<INamedTypeSymbol, Lazy<EquatableArray<TypeMetadataIdentity>>> _includingType =
            new(SymbolEqualityComparer.Default);

        public EquatableArray<TypeMetadataIdentity> Collect(INamedTypeSymbol type, bool includeType)
            => includeType
                ? _includingType.GetOrAdd(type, static symbol => new(() => CollectCore(symbol, includeType: true))).Value
                : _interfaceArguments.GetOrAdd(type, static symbol => new(() => CollectCore(symbol, includeType: false))).Value;
    }
}
