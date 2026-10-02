using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

internal static class TypeMetadataDependencyCollector
{
    public static EquatableArray<TypeMetadataIdentity> Collect(INamedTypeSymbol type, bool includeType = false)
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
}
