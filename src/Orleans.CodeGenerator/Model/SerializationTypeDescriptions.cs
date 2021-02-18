using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Compatibility;

namespace Orleans.CodeGenerator.Model
{
    internal class SerializationTypeDescriptions
    {
        public List<SerializerTypeDescription> SerializerTypes { get; } = new List<SerializerTypeDescription>();
        public HashSet<KnownTypeDescription> KnownTypes { get; } = new HashSet<KnownTypeDescription>(KnownTypeDescription.Comparer);
    }

    internal class SerializerTypeDescription
    {
        public bool OverrideExistingSerializer { get; set; }

        private INamedTypeSymbol target;

        public TypeSyntax SerializerTypeSyntax { get; set; }

        public INamedTypeSymbol Target
        {
            get => target;
            set => this.target = value?.OriginalDefinition?.ConstructedFrom;
        }

        public static IEqualityComparer<SerializerTypeDescription> TargetComparer { get; } = new TargetEqualityComparer();

        private sealed class TargetEqualityComparer : IEqualityComparer<SerializerTypeDescription>
        {
            public bool Equals(SerializerTypeDescription x, SerializerTypeDescription y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.GetType() != y.GetType()) return false;
                return SymbolEqualityComparer.Default.Equals(x.Target, y.Target);
            }

            public int GetHashCode(SerializerTypeDescription obj)
            {
                return obj.Target != null ? SymbolEqualityComparer.Default.GetHashCode(obj.Target) : 0;
            }
        }
    }

    public class KnownTypeDescription
    {
        public KnownTypeDescription(INamedTypeSymbol type)
        {
            this.Type = type.OriginalDefinition.ConstructedFrom;
        }

        public static IEqualityComparer<KnownTypeDescription> Comparer { get; } = new TypeTypeKeyEqualityComparer();

        public INamedTypeSymbol Type { get; }

        public string TypeKey => this.Type.OrleansTypeKeyString();

        private sealed class TypeTypeKeyEqualityComparer : IEqualityComparer<KnownTypeDescription>
        {
            public bool Equals(KnownTypeDescription x, KnownTypeDescription y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.GetType() != y.GetType()) return false;
                return SymbolEqualityComparer.Default.Equals(x.Type, y.Type);
            }

            public int GetHashCode(KnownTypeDescription obj)
            {
                unchecked
                {
                    return ((obj.Type != null ? SymbolEqualityComparer.Default.GetHashCode(obj.Type) : 0) * 397);
                }
            }
        }
    }
}
