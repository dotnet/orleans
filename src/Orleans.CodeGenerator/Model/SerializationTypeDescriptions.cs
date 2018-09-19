using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
                return Equals(x.Target, y.Target);
            }

            public int GetHashCode(SerializerTypeDescription obj)
            {
                return obj.Target != null ? obj.Target.GetHashCode() : 0;
            }
        }
    }

    public class KnownTypeDescription
    {
        private sealed class TypeTypeKeyEqualityComparer : IEqualityComparer<KnownTypeDescription>
        {
            public bool Equals(KnownTypeDescription x, KnownTypeDescription y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (ReferenceEquals(x, null)) return false;
                if (ReferenceEquals(y, null)) return false;
                if (x.GetType() != y.GetType()) return false;
                return Equals(x.type, y.type) && string.Equals(x.TypeKey, y.TypeKey);
            }

            public int GetHashCode(KnownTypeDescription obj)
            {
                unchecked
                {
                    return ((obj.type != null ? obj.type.GetHashCode() : 0) * 397) ^ (obj.TypeKey != null ? obj.TypeKey.GetHashCode() : 0);
                }
            }
        }

        public static IEqualityComparer<KnownTypeDescription> Comparer { get; } = new TypeTypeKeyEqualityComparer();

        private INamedTypeSymbol type;

        public INamedTypeSymbol Type
        {
            get => type;
            set => type = value?.OriginalDefinition?.ConstructedFrom;
        }

        public string TypeKey { get; set; }
    }
}
