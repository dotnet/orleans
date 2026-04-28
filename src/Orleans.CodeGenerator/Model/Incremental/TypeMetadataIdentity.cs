using System;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Identifies a type by its Roslyn metadata name and containing assembly.
    /// </summary>
    internal readonly struct TypeMetadataIdentity : IEquatable<TypeMetadataIdentity>
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

        public bool Equals(TypeMetadataIdentity other)
            => string.Equals(MetadataName, other.MetadataName, StringComparison.Ordinal)
                && string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && string.Equals(AssemblyIdentity, other.AssemblyIdentity, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is TypeMetadataIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(MetadataName ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(AssemblyIdentity ?? string.Empty);
                return hash;
            }
        }

        public static bool operator ==(TypeMetadataIdentity left, TypeMetadataIdentity right) => left.Equals(right);
        public static bool operator !=(TypeMetadataIdentity left, TypeMetadataIdentity right) => !left.Equals(right);

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
}
