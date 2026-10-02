using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Identifies a type by its Roslyn metadata name and containing assembly.
/// </summary>
internal readonly record struct TypeMetadataIdentity
{
    private static readonly ConditionalWeakTable<INamedTypeSymbol, CachedIdentity> Cache = new();

    public TypeMetadataIdentity(string metadataName, string assemblyName, string assemblyIdentity)
    {
        MetadataName = metadataName ?? string.Empty;
        AssemblyName = assemblyName ?? string.Empty;
        AssemblyIdentity = assemblyIdentity ?? string.Empty;
    }

    public string MetadataName { get; }
    public string AssemblyName { get; }
    public string AssemblyIdentity { get; }
    public bool IsEmpty => string.IsNullOrEmpty(MetadataName);

    public static TypeMetadataIdentity Empty { get; } = new TypeMetadataIdentity(
        metadataName: string.Empty,
        assemblyName: string.Empty,
        assemblyIdentity: string.Empty);

    public static TypeMetadataIdentity Create(INamedTypeSymbol symbol)
    {
        if (symbol is null)
        {
            return Empty;
        }

        return Cache.GetValue(symbol.OriginalDefinition, static definition => new(CreateCore(definition))).Value;
    }

    private static TypeMetadataIdentity CreateCore(INamedTypeSymbol definition)
    {
        var assembly = definition.ContainingAssembly;
        return new TypeMetadataIdentity(
            GetMetadataName(definition),
            assembly?.Identity.Name ?? string.Empty,
            assembly?.Identity.GetDisplayName() ?? string.Empty);
    }

    private static string GetMetadataName(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder();
        var ns = symbol.ContainingNamespace;
        if (ns is not null && !ns.IsGlobalNamespace)
        {
            builder.Append(ns.ToDisplayString());
            builder.Append('.');
        }

        AppendMetadataName(builder, symbol);
        return builder.ToString();

        static void AppendMetadataName(StringBuilder builder, INamedTypeSymbol current)
        {
            if (current.ContainingType is { } containingType)
            {
                AppendMetadataName(builder, containingType);
                builder.Append('+');
            }

            builder.Append(current.MetadataName);
        }
    }

    private sealed class CachedIdentity(TypeMetadataIdentity value)
    {
        public TypeMetadataIdentity Value { get; } = value;
    }
}
