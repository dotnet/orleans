using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Utilities;

namespace Orleans.CodeGenerator.Analysis
{
    internal class SerializerTypeAnalyzer
    {
        private readonly INamedTypeSymbol copierMethodAttribute;
        private readonly INamedTypeSymbol serializerMethodAttribute;
        private readonly INamedTypeSymbol deserializerMethodAttribute;
        private readonly INamedTypeSymbol serializerAttribute;

        public SerializerTypeAnalyzer(
            INamedTypeSymbol copierMethodAttribute,
            INamedTypeSymbol serializerMethodAttribute,
            INamedTypeSymbol deserializerMethodAttribute,
            INamedTypeSymbol serializerAttribute)
        {
            this.copierMethodAttribute = copierMethodAttribute;
            this.serializerMethodAttribute = serializerMethodAttribute;
            this.deserializerMethodAttribute = deserializerMethodAttribute;
            this.serializerAttribute = serializerAttribute;
        }

        public static SerializerTypeAnalyzer Create(WellKnownTypes wellKnownTypes) => new SerializerTypeAnalyzer(
            wellKnownTypes.CopierMethodAttribute,
            wellKnownTypes.SerializerMethodAttribute,
            wellKnownTypes.DeserializerMethodAttribute,
            wellKnownTypes.SerializerAttribute);

        public bool IsSerializer(INamedTypeSymbol type, out INamedTypeSymbol[] targetTypes)
        {
            if (!HasSerializerMethods(type))
            {
                targetTypes = Array.Empty<INamedTypeSymbol>();
                return false;
            }

            if (type.GetAttributes(this.serializerAttribute, out var attrs))
            {
                var targets = attrs.Select(attr => (INamedTypeSymbol)attr.ConstructorArguments.First().Value);
                targetTypes = targets.ToArray();
            }
            else
            {
                // This is only a self-serializing type.
                // Self-serializing types do not require the [Serializer(typeof(X))] attribute.
                targetTypes = new[] { type };
            }

            return true;
        }

        private bool HasSerializerMethods(ITypeSymbol type)
        {
            var (hasCopier, hasSerializer, hasDeserializer) = (false, false, false);
            foreach (var member in type.GetMembers())
            {
                if (!(member is IMethodSymbol method)) continue;
                if (method.HasAttribute(this.copierMethodAttribute)) hasCopier = true;
                else if (method.HasAttribute(this.serializerMethodAttribute)) hasSerializer = true;
                else if (method.HasAttribute(this.deserializerMethodAttribute)) hasDeserializer = true;

                if (hasCopier && hasSerializer && hasDeserializer) return true;
            }

            return false;
        }
    }
}
