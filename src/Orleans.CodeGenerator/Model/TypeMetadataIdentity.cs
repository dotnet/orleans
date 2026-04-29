using System.Text;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Identifies a type by its Roslyn metadata name and containing assembly.
/// </summary>
internal readonly record struct TypeMetadataIdentity
{
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

        var originalDefinition = symbol.OriginalDefinition;
        var assembly = originalDefinition.ContainingAssembly;
        return new TypeMetadataIdentity(
            GetMetadataName(originalDefinition),
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
}
