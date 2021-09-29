using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Utilities
{
    internal static class SymbolExtensions
    {
        public static string GetNamespaceName(this INamespaceOrTypeSymbol type)
        {
            // global::A.B.C
            var result = new StringBuilder();
            Recurse(type, result);
            return result.ToString();

            void Recurse(INamespaceOrTypeSymbol symbol, StringBuilder sb)
            {
                var ns = symbol.ContainingNamespace;
                if (ns != null && !ns.IsGlobalNamespace)
                {
                    Recurse(ns, sb);
                    if (sb.Length > 0) sb.Append('.');
                    sb.Append(ns.Name);
                }
            }
        }

        public static IEnumerable<ITypeParameterSymbol> GetHierarchyTypeParameters(this INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
            {
                foreach (var t in type.ContainingType.GetHierarchyTypeParameters()) yield return t;
            }

            foreach (var t in type.TypeParameters) yield return t;
        }

        public static IEnumerable<ITypeSymbol> GetHierarchyTypeArguments(this INamedTypeSymbol type)
        {
            if (type.ContainingType != null)
            {
                foreach (var t in type.ContainingType.GetHierarchyTypeArguments()) yield return t;
            }

            foreach (var t in type.TypeArguments) yield return t;
        }

        public static IEnumerable<INamedTypeSymbol> GetNestedHierarchy(this INamedTypeSymbol type)
        {
            while (type != null)
            {
                yield return type;
                type = type.ContainingType;
            }
        }

        public static string GetGenericTypeSuffix(this INamedTypeSymbol type)
        {
            var numParams = type.GetHierarchyTypeParameters().Count();
            if (numParams == 0) return string.Empty;
            return '<' + new string(',', numParams - 1) + '>';
        }

        public static string GetSuitableClassName(this INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Interface && type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                throw new NotSupportedException($"Type {type} has kind {type.TypeKind}, which is not supported.");

            if (type.IsImplicitlyDeclared)
                throw new NotSupportedException($"Type {type} is implicitly declared, which is not supported.");

            var index = type.MetadataName.IndexOf('`');
            var trimmed = index > 0 ? type.MetadataName.Substring(0, index) : type.MetadataName;

            var isInterface = type.TypeKind == TypeKind.Interface;
            if (isInterface) return GetClassNameFromInterfaceName(trimmed);
            return trimmed;

            string GetClassNameFromInterfaceName(string interfaceName)
            {
                if (interfaceName.StartsWith("i", StringComparison.OrdinalIgnoreCase))
                {
                    return interfaceName.Substring(1);
                }

                return interfaceName;
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetDeclaredTypes(this IAssemblySymbol reference)
        {
            foreach (var module in reference.Modules)
            {
                foreach (var type in GetDeclaredTypes(module.GlobalNamespace))
                {
                    yield return type;
                }
            }

            IEnumerable<INamedTypeSymbol> GetDeclaredTypes(INamespaceOrTypeSymbol ns)
            {
                foreach (var member in ns.GetMembers())
                {
                    switch (member)
                    {
                        case INamespaceSymbol nestedNamespace:
                            foreach (var nested in GetDeclaredTypes(nestedNamespace)) yield return nested;
                            break;
                        case ITypeSymbol type:
                            if (type is INamedTypeSymbol namedType) yield return namedType;
                            foreach (var nested in GetDeclaredTypes(type)) yield return nested;
                            break;
                    }
                }
            }
        }

        public static bool HasInterface(this ITypeSymbol typeSymbol, INamedTypeSymbol interfaceTypeSymbol)
        {
            return typeSymbol.AllInterfaces.Contains(interfaceTypeSymbol);
        }

        public static bool HasBaseType(this ITypeSymbol typeSymbol, INamedTypeSymbol baseType)
        {
            for (; typeSymbol != null; typeSymbol = typeSymbol.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType, typeSymbol)) return true;
            }
            return false;
        }

        public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol attributeType)
        {
            var attributes = symbol.GetAttributes();
            foreach (var attr in attributes)
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType)) return true;
            }

            return false;
        }

        public static bool HasAttribute(this ISymbol symbol, string attributeTypeName)
        {
            var attributes = symbol.GetAttributes();
            foreach (var attr in attributes)
            {
                if (attr.AttributeClass.Name.Equals(attributeTypeName)) return true;
            }

            return false;
        }

        public static bool GetAttribute(this ISymbol symbol, INamedTypeSymbol attributeType, out AttributeData attribute)
        {
            if (!symbol.GetAttributes(attributeType, out var attributes))
            {
                attribute = null;
                return false;
            }

            if (attributes.Length > 1) throw new InvalidOperationException($"Symbol {symbol} has {attributes.Length} attributes of type {attributeType}.");

            attribute = attributes[0];
            return true;
        }

        /// <summary>
        /// Gets all attributes which are assignable to the specified attribute type.
        /// </summary>
        public static bool GetAttributes(this ISymbol symbol, INamedTypeSymbol attributeType, out AttributeData[] attributes)
        {
            var temp = default(List<AttributeData>);
            foreach (var attr in symbol.GetAttributes())
            {
                if (!attr.AttributeClass.HasBaseType(attributeType)) continue;

                if (temp == null) temp = new List<AttributeData>();
                temp.Add(attr);
            }

            attributes = temp?.ToArray();
            return attributes != null && attributes.Length > 0;
        }

        public static string GetValidIdentifier(this ITypeSymbol type)
        {
            switch (type)
            {
                case INamedTypeSymbol named when !named.IsGenericType: return $"{named.Name}";
                case INamedTypeSymbol named:
                    return $"{named.Name}_{string.Join("_", named.TypeArguments.Select(GetValidIdentifier))}";
                case IArrayTypeSymbol array:
                    return $"{GetValidIdentifier(array.ElementType)}_{array.Rank}";
                case IPointerTypeSymbol pointer:
                    return $"{GetValidIdentifier(pointer.PointedAtType)}_ptr";
                case ITypeParameterSymbol parameter:
                    return $"{parameter.Name}";
                default:
                    throw new NotSupportedException($"Unable to format type of kind {type.GetType()} with name \"{type.Name}\"");
            }
        }

        public static IEnumerable<TSymbol> GetDeclaredInstanceMembers<TSymbol>(this ITypeSymbol type) where TSymbol : ISymbol
        {
            foreach (var candidate in type.GetMembers())
            {
                if (candidate.IsStatic) continue;
                if (candidate is TSymbol symbol) yield return symbol;
            }
        }

        public static IEnumerable<TSymbol> GetInstanceMembers<TSymbol>(this ITypeSymbol type) where TSymbol : ISymbol
        {
            foreach (var candidate in type.GetMembers())
            {
                if (candidate.IsStatic) continue;
                if (candidate is TSymbol symbol) yield return symbol;
            }

            var baseType = type.BaseType;
            if (baseType != null)
            {
                foreach (var t in baseType.GetInstanceMembers<TSymbol>()) yield return t;
            }
        }

        public static TSymbol Member<TSymbol>(this ITypeSymbol type, string name, Func<TSymbol, bool> predicate = null) where TSymbol : class
        {
            var methods = type.GetMembers(name).OfType<TSymbol>();
            if (predicate != null) methods = methods.Where(predicate);

            var results = methods.ToList();

            if (results.Count == 0)
            {
                throw new KeyNotFoundException(
                    $"Type {type} does not have a member of kind {typeof(TSymbol)} named {name}{(predicate == null ? String.Empty : " matching the specified predicate.")}");
            }

            if (results.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Type {type} has multiple members of kind {typeof(TSymbol)} named {name}{(predicate == null ? String.Empty : " matching the specified predicate.")}");
            }

            return results[0];
        }

        public static IMethodSymbol Method(this ITypeSymbol type, string name, Func<IMethodSymbol, bool> predicate = null) => type.Member(name, predicate);

        public static IMethodSymbol Method(this ITypeSymbol type, string name, params INamedTypeSymbol[] parameters) =>
            type.Member<IMethodSymbol>(name, m => m.Parameters.Select(p => p.Type).SequenceEqual(parameters, SymbolEqualityComparer.Default));

        public static IPropertySymbol Property(this ITypeSymbol type, string name) => type.Member<IPropertySymbol>(name);

        public static INamedTypeSymbol WithoutTypeParameters(this INamedTypeSymbol type)
        {
            if (type.IsGenericType && !type.IsUnboundGenericType) return type.ConstructUnboundGenericType();
            return type;
        }
    }
}