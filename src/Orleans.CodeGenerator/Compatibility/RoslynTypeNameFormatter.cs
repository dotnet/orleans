using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Utilities;

namespace Orleans.CodeGenerator.Compatibility
{
    /// <summary>
    /// Utility methods for formatting <see cref="ITypeSymbol"/> instances in a way which can be later parsed by <see cref="Type.GetType(string)"/>.
    /// </summary>
    internal static class RoslynTypeNameFormatter
    {
        private static readonly char[] SimpleNameTerminators = { '`', '*', '[', '&' };
        
        public enum Style
        {
            FullName,
            RuntimeTypeNameFormatter
        }
        
        /// <summary>
        /// Returns a <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
        /// </summary>
        public static string Format(ITypeSymbol type, Style style)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var builder = new StringBuilder();
            Format(builder, type, isElementType: false, style: style, depth: 0);
            return builder.ToString();
        }

        private static void Format(StringBuilder builder, ITypeSymbol type, bool isElementType, Style style, int depth)
        {
            switch (type)
            {
                case IPointerTypeSymbol pointer:
                    Format(builder, pointer.PointedAtType, isElementType: true, style: style, depth: depth + 1);
                    AddPointerSymbol(builder);
                    break;
                case IArrayTypeSymbol array:
                    Format(builder, array.ElementType, isElementType: true, style: style, depth: depth + 1);
                    AddArrayRank(builder, array);
                    break;
                case INamedTypeSymbol named:
                    named = named.TupleUnderlyingType ?? named;
                    AddNamespace(builder, type);
                    AddClassName(builder, named);
                    AddGenericParameters(builder, named, style, depth + 1);
                    break;
                case ITypeParameterSymbol parameter:
                    if (style != Style.FullName)
                    {
                        AddNamespace(builder, type);
                        AddClassName(builder, parameter);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Type symbol {type} of type {type.GetType()} (interfaces: {string.Join(", ", type.AllInterfaces.Select(i => i.ToString()))}) is not supported");
            }

            // Types which are used as elements are not formatted with their assembly name, since that is added after the
            // element type's adornments.
            if (!isElementType)
            {
                switch (style)
                {
                    case Style.FullName:
                        if (depth != 0) AddAssembly(builder, type, style);
                        break;
                    case Style.RuntimeTypeNameFormatter:
                        AddAssembly(builder, type, style);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(style), style, null);
                }
            }
        }

        private static void AddNamespace(StringBuilder builder, ITypeSymbol type)
        {
            var namespaceName = type.GetNamespaceName();
            if (string.IsNullOrWhiteSpace(namespaceName)) return;
            builder.Append(namespaceName);
            builder.Append('.');
        }

        private static void AddClassName(StringBuilder builder, INamedTypeSymbol type)
        {
            // Format the declaring type.
            if (type.ContainingType != null)
            {
                AddClassName(builder, type.ContainingType);
                builder.Append('+');
            }

            // Format the simple type name.
            var index = type.Name.IndexOfAny(SimpleNameTerminators);
            builder.Append(index > 0 ? type.Name.Substring(0, index) : type.Name);

            // Format this type's generic arity.
            AddGenericArity(builder, type);
        }

        private static void AddClassName(StringBuilder builder, ITypeParameterSymbol type)
        {
            // Format the declaring type.
            if (type.ContainingType != null)
            {
                AddClassName(builder, type.ContainingType);
                builder.Append('+');
            }

            // Format the simple type name.
            var index = type.Name.IndexOfAny(SimpleNameTerminators);
            builder.Append(index > 0 ? type.Name.Substring(0, index) : type.Name);
        }

        private static List<ITypeSymbol> GetHierarchyTypeArguments(ITypeSymbol type)
        {
            return Helper(type).ToList();

            IEnumerable<ITypeSymbol> Helper(ITypeSymbol t)
            {
                if (t.ContainingType != null)
                {
                    foreach (var arg in Helper(t.ContainingType)) yield return arg;
                }

                if (t is INamedTypeSymbol named)
                    foreach (var arg in named.TypeArguments)
                        yield return arg;
            }
        }


        private static void AddGenericParameters(StringBuilder builder, INamedTypeSymbol type, Style style, int depth)
        {
            // Generic type definitions (eg, List<> without parameters) and non-generic types do not include any
            // parameters in their formatting.
            if (type.IsUnboundGenericType) return;

            var args = GetHierarchyTypeArguments(type);
            if (args.Count == 0) return;
            if (args.All(a => a is ITypeParameterSymbol)) return;

            builder.Append('[');
            for (var i = 0; i < args.Count; i++)
            {
                builder.Append('[');
                Format(builder, args[i], isElementType: false, style: style, depth: depth);
                builder.Append(']');
                if (i + 1 < args.Count) builder.Append(",");
            }

            builder.Append(']');
        }

        private static void AddGenericArity(StringBuilder builder, INamedTypeSymbol type)
        {
            if (!type.IsGenericType) return;

            if (type.TypeArguments.Length == 0) return;

            builder.Append('`');
            builder.Append(type.TypeArguments.Length);
        }

        private static void AddPointerSymbol(StringBuilder builder)
        {
            builder.Append('*');
        }

        private static void AddArrayRank(StringBuilder builder, IArrayTypeSymbol type)
        {
            builder.Append('[');
            builder.Append(',', type.Rank - 1);
            builder.Append(']');
        }

        private static void AddAssembly(StringBuilder builder, ITypeSymbol type, Style style)
        {
            switch (type)
            {
                case IPointerTypeSymbol p:
                    AddAssembly(builder, p.PointedAtType, style);
                    break;
                case IArrayTypeSymbol a:
                    AddAssembly(builder, a.ElementType, style);
                    break;
                default:
                    var assembly = type.ContainingAssembly;
                    if (assembly == null) return;

                    switch (style)
                    {
                        case Style.FullName:
                            builder.Append(", ");
                            builder.Append(assembly.Identity.GetDisplayName());
                            break;
                        case Style.RuntimeTypeNameFormatter:
                            if (RoslynTypeHelper.IsSystemNamespace(type.ContainingNamespace)) return;
                            builder.Append(",");
                            builder.Append(assembly.Identity.Name);
                            break;
                    }

                    break;
            }
        }
    }
}