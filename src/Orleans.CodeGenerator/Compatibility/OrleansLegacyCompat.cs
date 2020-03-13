using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Utilities;

namespace Orleans.CodeGenerator.Compatibility
{
    internal static class OrleansLegacyCompat
    {
        public static int GetMethodId(this WellKnownTypes wellKnownTypes, IMethodSymbol methodInfo)
        {
            if (GetAttribute(methodInfo, wellKnownTypes.MethodIdAttribute) is AttributeData attr)
            {
                return (int)attr.ConstructorArguments.First().Value;
            }

            var result = FormatMethodForMethodIdComputation(methodInfo);
            return CalculateIdHash(result);
        }

        internal static string FormatMethodForMethodIdComputation(IMethodSymbol methodInfo)
        {
            var result = new StringBuilder(methodInfo.Name);

            if (methodInfo.IsGenericMethod)
            {
                result.Append('<');
                var first = true;
                foreach (var arg in methodInfo.TypeArguments)
                {
                    if (!first) result.Append(',');
                    else first = false;
                    result.Append(RoslynTypeNameFormatter.Format(arg, RoslynTypeNameFormatter.Style.RuntimeTypeNameFormatter));
                }

                result.Append('>');
            }

            {
                result.Append('(');
                var parameters = methodInfo.Parameters;
                var first = true;
                foreach (var parameter in parameters)
                {
                    if (!first)
                        result.Append(',');
                    var parameterType = parameter.Type;
                    switch (parameterType)
                    {
                        case ITypeParameterSymbol _:
                            result.Append(parameterType.Name);
                            break;
                        default:
                            result.Append(RoslynTypeNameFormatter.Format(parameterType, RoslynTypeNameFormatter.Style.RuntimeTypeNameFormatter));
                            break;
                    }

                    first = false;
                }
            }

            result.Append(')');
            return result.ToString();
        }

        public static int GetTypeId(this WellKnownTypes wellKnownTypes, INamedTypeSymbol type)
        {
            if (GetAttribute(type, wellKnownTypes.TypeCodeOverrideAttribute) is AttributeData attr)
            {
                return (int)attr.ConstructorArguments.First().Value;
            }

            var fullName = FormatTypeForIdComputation(type);
            return CalculateIdHash(fullName);
        }

        private static AttributeData GetAttribute(ISymbol type, ITypeSymbol attributeType)
        {
            var attrs = type.GetAttributes();
            foreach (var attr in attrs)
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                {
                    return attr;
                }
            }

            return null;
        }

        private static int CalculateIdHash(string text)
        {
            var sha = SHA256.Create();
            var hash = 0;
            try
            {
                var data = Encoding.Unicode.GetBytes(text);
                var result = sha.ComputeHash(data);
                for (var i = 0; i < result.Length; i += 4)
                {
                    var tmp = (result[i] << 24) | (result[i + 1] << 16) | (result[i + 2] << 8) | result[i + 3];
                    hash = hash ^ tmp;
                }
            }
            finally
            {
                sha.Dispose();
            }

            return hash;
        }

        internal static string FormatTypeForIdComputation(INamedTypeSymbol symbol) =>
            GetTemplatedName(
                GetFullName(symbol),
                symbol,
                symbol.TypeArguments,
                t => false);

        public static ushort GetVersion(this WellKnownTypes wellKnownTypes, ISymbol symbol)
        {
            if (GetAttribute(symbol, wellKnownTypes.VersionAttribute) is AttributeData attr)
            {
                return (ushort)attr.ConstructorArguments.First().Value;
            }

            // Return the default version
            return 0;
        }

        /// <summary>
        /// Returns true if the provided type is a grain interface.
        /// </summary>
        public static bool IsGrainInterface(this WellKnownTypes types, INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Interface) return false;

            var orig = type.OriginalDefinition;
            return orig.AllInterfaces.Contains(types.IAddressable) && !IsGrainMarkerInterface(types, orig);

            bool IsGrainMarkerInterface(WellKnownTypes l, INamedTypeSymbol t)
            {
                return Eq(t, l.IGrainObserver) ||
                       Eq(t, l.IAddressable) ||
                       Eq(t, l.IGrainExtension) ||
                       Eq(t, l.IGrain) ||
                       Eq(t, l.IGrainWithGuidKey) ||
                       Eq(t, l.IGrainWithIntegerKey) ||
                       Eq(t, l.IGrainWithStringKey) ||
                       Eq(t, l.IGrainWithGuidCompoundKey) ||
                       Eq(t, l.IGrainWithIntegerCompoundKey) ||
                       Eq(t, l.ISystemTarget);

                static bool Eq(ISymbol left, ISymbol right) => SymbolEqualityComparer.Default.Equals(left, right);
            }
        }

        /// <summary>
        /// Returns true if the provided type is a grain implementation.
        /// </summary>
        public static bool IsGrainClass(this WellKnownTypes types, INamedTypeSymbol type)
        {
            if (type.TypeKind != TypeKind.Class) return false;

            var orig = type.OriginalDefinition;
            return HasBase(orig, types.Grain) && !IsMarkerType(types, orig);

            bool IsMarkerType(WellKnownTypes l, INamedTypeSymbol t)
            {
                return SymbolEqualityComparer.Default.Equals(t, l.Grain) || SymbolEqualityComparer.Default.Equals(t, l.GrainOfT);
            }

            bool HasBase(INamedTypeSymbol t, INamedTypeSymbol baseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t.BaseType, baseType)) return true;
                if (t.BaseType != null) return HasBase(t.BaseType, baseType);
                return false;
            }
        }
        public static string OrleansTypeKeyString(this ITypeSymbol t)
        {
            var sb = new StringBuilder();
            OrleansTypeKeyString(t, sb);

            return sb.ToString();
        }

        private static void OrleansTypeKeyString(ITypeSymbol t, StringBuilder sb)
        {
            var namedType = t as INamedTypeSymbol;

            // Check if the type is a non-constructed generic type.
            if (namedType != null && IsGenericTypeDefinition(namedType, out var typeParamsLength))
            {
                GetBaseTypeKey(t, sb);
                sb.Append('\'');
                sb.Append(typeParamsLength);
            }
            else if (namedType != null && namedType.IsGenericType)
            {
                GetBaseTypeKey(t, sb);
                sb.Append('<');
                var first = true;
                foreach (var genericArgument in namedType.GetHierarchyTypeArguments())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    OrleansTypeKeyString(genericArgument, sb);
                }

                sb.Append('>');
            }
            else if (t is IArrayTypeSymbol arrayType)
            {
                OrleansTypeKeyString(arrayType.ElementType, sb);

                sb.Append('[');
                if (arrayType.Rank > 1)
                {
                    sb.Append(',', arrayType.Rank - 1);
                }
                sb.Append(']');
            }
            else if (t is IPointerTypeSymbol pointerType)
            {
                OrleansTypeKeyString(pointerType.PointedAtType, sb);

                sb.Append("*");
            }
            else
            {
                GetBaseTypeKey(t, sb);
            }
        }

        private static void GetBaseTypeKey(ITypeSymbol type, StringBuilder sb)
        {
            var namespacePrefix = "";
            if (!RoslynTypeHelper.IsSystemNamespace(type.ContainingNamespace))
            {
                namespacePrefix = type.ContainingNamespace.ToString() + '.';
            }

            if (type.DeclaredAccessibility == Accessibility.Public && type.ContainingType != null)
            {
                sb.Append(namespacePrefix);
                OrleansTypeKeyString(type.OriginalDefinition?.ContainingType ?? type.ContainingType, sb);
                sb.Append('.').Append(type.Name);
            }
            else
            {
                sb.Append(namespacePrefix).Append(type.Name);
            }

            if (type is INamedTypeSymbol namedType && namedType.IsGenericType && namedType.TypeArguments.Length > 0)
            {
                sb.Append('`').Append(namedType.TypeArguments.Length);
            }
        }

        public static string GetTemplatedName(ITypeSymbol type, Func<ITypeSymbol, bool> fullName = null)
        {
            if (fullName == null) fullName = _ => true;

            switch (type)
            {
                case IArrayTypeSymbol array:
                    return GetTemplatedName(array.ElementType, fullName)
                           + "["
                           + new string(',', array.Rank - 1)
                           + "]";
                case INamedTypeSymbol named when named.IsGenericType:
                    return GetTemplatedName(GetSimpleTypeName(named, fullName), named, named.TypeArguments, fullName);
                case INamedTypeSymbol named:
                    return GetSimpleTypeName(named, fullName);
                case ITypeParameterSymbol parameter:
                    return parameter.Name;
                default:
                    throw new NotSupportedException($"Symbol {type} of type {type.GetType()} is not supported.");
            }
        }

        public static string GetTemplatedName(string baseName, INamedTypeSymbol type, ImmutableArray<ITypeSymbol> genericArguments, Func<ITypeSymbol, bool> fullName)
        {
            if (!type.IsGenericType || type.ContainingType != null && type.ContainingType.IsGenericType) return baseName;
            var s = baseName;
            s += "<";
            s += GetGenericTypeArgs(genericArguments, fullName);
            s += ">";
            return s;
        }

        public static string GetGenericTypeArgs(IEnumerable<ITypeSymbol> args, Func<ITypeSymbol, bool> fullName)
        {
            var result = string.Empty;

            var first = true;
            foreach (var genericParameter in args)
            {
                if (!first)
                {
                    result += ",";
                }

                if (genericParameter is INamedTypeSymbol named && !named.IsGenericType)
                {
                    result += GetSimpleTypeName(named, fullName);
                }
                else
                {
                    result += GetTemplatedName(genericParameter, fullName);
                }

                first = false;
            }

            return result;
        }

        public static string GetSimpleTypeName(ITypeSymbol type, Func<ITypeSymbol, bool> fullName = null)
        {
            var named = type as INamedTypeSymbol;
            if (type.ContainingType != null)
            {
                if (type.ContainingType.IsGenericType)
                {
                    return GetTemplatedName(
                               GetUntemplatedTypeName(type.ContainingType.Name),
                               type.ContainingType,
                               named?.TypeArguments ?? default(ImmutableArray<ITypeSymbol>),
                               _ => true) + "." + GetUntemplatedTypeName(type.Name);
                }

                return GetTemplatedName(type.ContainingType) + "." + GetUntemplatedTypeName(type.Name);
            }

            if (named == null || named.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(type) ? GetFullName(type) : type.Name);

            return fullName != null && fullName(type) ? GetFullName(type) : type.Name;
        }

        public static string GetUntemplatedTypeName(string typeName)
        {
            var i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;
        }

        public static string GetSimpleTypeName(string typeName)
        {
            var i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('[');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;
        }

        public static string GetFullName(ITypeSymbol t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            if (t.ContainingType != null && !(t is ITypeParameterSymbol))
            {
                return $"{t.GetNamespaceName()}.{t.ContainingType.Name}.{t.Name}{GetArity(t)}";
            }

            if (t is IArrayTypeSymbol array)
            {
                return GetFullName(array.ElementType)
                       + "["
                       + new string(',', array.Rank - 1)
                       + "]";
            }

            return RoslynTypeNameFormatter.Format(t, RoslynTypeNameFormatter.Style.FullName); // ?? (t is ITypeParameterSymbol) ? t.Name : t.GetNamespaceName() + "." + t.Name;

            string GetArity(ITypeSymbol type)
            {
                if (!(type is INamedTypeSymbol named)) return string.Empty;
                if (named.TypeArguments.Length > 0) return $"`{named.TypeArguments.Length}";
                return string.Empty;
            }
        }

        static bool IsGenericTypeDefinition(INamedTypeSymbol type, out int typeParamsLength)
        {
            if (type.IsUnboundGenericType)
            {
                typeParamsLength = type.GetHierarchyTypeArguments().Count();
                return true;
            }

            if (type.IsGenericType && type.GetNestedHierarchy().All(t => SymbolEqualityComparer.Default.Equals(t.ConstructedFrom, t)))
            {
                typeParamsLength = type.GetHierarchyTypeArguments().Count();
                return true;
            }

            typeParamsLength = 0;
            return false;
        }
    }
}