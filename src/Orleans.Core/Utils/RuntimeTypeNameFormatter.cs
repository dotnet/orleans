using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Utilities
{
    /// <summary>
    /// Utility methods for formatting <see cref="Type"/> instances in a way which can be parsed by
    /// <see cref="Type.GetType(string)"/> and <see cref="RuntimeTypeNameParser"/>.
    /// </summary>
    public static class RuntimeTypeNameFormatter
    {
        private static readonly Assembly SystemAssembly = typeof(int).Assembly;
        private static readonly char[] SimpleNameTerminators = { '`', '*', '[', '&' };

        private static readonly ConcurrentDictionary<Type, string> Cache = new ConcurrentDictionary<Type, string>();

        static RuntimeTypeNameFormatter()
        {
            IncludeAssemblyName = type => !SystemAssembly.Equals(type.Assembly);
        }

        /// <summary>
        /// Gets or sets the delegate used to determine whether an assembly name should be printed for the provided type.
        /// </summary>
        public static Func<Type, bool> IncludeAssemblyName { get; set; }

        /// <summary>
        /// Returns a <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
        /// </summary>
        /// <param name="type">The type to format.</param>
        /// <returns>
        /// A <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
        /// </returns>
        public static string Format(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            if (!Cache.TryGetValue(type, out var result))
            {
                string FormatType(Type t)
                {
                    var builder = new StringBuilder();
                    Format(builder, t, isElementType: false);
                    return builder.ToString();
                }

                result = Cache.GetOrAdd(type, FormatType);
            }

            return result;
        }

        private static void Format(StringBuilder builder, Type type, bool isElementType)
        {
            // Arrays, pointers, and by-ref types are all element types and need to be formatted with their own adornments.
            if (type.HasElementType)
            {
                // Format the element type.
                Format(builder, type.GetElementType(), isElementType: true);

                // Format this type's adornments to the element type.
                AddArrayRank(builder, type);
                AddPointerSymbol(builder, type);
                AddByRefSymbol(builder, type);
            }
            else
            {
                AddNamespace(builder, type);
                AddClassName(builder, type);
                AddGenericParameters(builder, type);
            }

            // Types which are used as elements are not formatted with their assembly name, since that is added after the
            // element type's adornments.
            if (!isElementType && IncludeAssemblyName(type))
            {
                AddAssembly(builder, type);
            }
        }

        private static void AddNamespace(StringBuilder builder, Type type)
        {
            if (string.IsNullOrWhiteSpace(type.Namespace)) return;
            builder.Append(type.Namespace);
            builder.Append('.');
        }

        private static void AddClassName(StringBuilder builder, Type type)
        {
            // Format the declaring type.
            if (type.IsNested)
            {
                AddClassName(builder, type.DeclaringType);
                builder.Append('+');
            }

            // Format the simple type name.
            var index = type.Name.IndexOfAny(SimpleNameTerminators);
            builder.Append(index > 0 ? type.Name.Substring(0, index) : type.Name);

            // Format this type's generic arity.
            AddGenericArity(builder, type);
        }

        private static void AddGenericParameters(StringBuilder builder, Type type)
        {
            // Generic type definitions (eg, List<> without parameters) and non-generic types do not include any
            // parameters in their formatting.
            if (!type.IsConstructedGenericType || type.ContainsGenericParameters) return;

            var args = type.GetGenericArguments();
            builder.Append('[');
            for (var i = 0; i < args.Length; i++)
            {
                builder.Append('[');
                Format(builder, args[i], isElementType: false);
                builder.Append(']');
                if (i + 1 < args.Length) builder.Append(',');
            }

            builder.Append(']');
        }

        private static void AddGenericArity(StringBuilder builder, Type type)
        {
            if (!type.IsGenericType) return;

            // The arity is the number of generic parameters minus the number of generic parameters in the declaring types.
            var baseTypeParameterCount =
                type.IsNested ? type.DeclaringType.GetGenericArguments().Length : 0;
            var arity = type.GetGenericArguments().Length - baseTypeParameterCount;

            // If all of the generic parameters are in the declaring types then this type has no parameters of its own.
            if (arity == 0) return;

            builder.Append('`');
            builder.Append(arity);
        }

        private static void AddPointerSymbol(StringBuilder builder, Type type)
        {
            if (!type.IsPointer) return;
            builder.Append('*');
        }

        private static void AddByRefSymbol(StringBuilder builder, Type type)
        {
            if (!type.IsByRef) return;
            builder.Append('&');
        }

        private static void AddArrayRank(StringBuilder builder, Type type)
        {
            if (!type.IsArray) return;
            builder.Append('[');
            builder.Append(',', type.GetArrayRank() - 1);
            builder.Append(']');
        }

        private static void AddAssembly(StringBuilder builder, Type type)
        {
            // Do not include the assembly name for the system assembly.
            if (IsSystemNamespace(type)) return;
            builder.Append(',');
            builder.Append(type.Assembly.GetName().Name);
        }

        private static bool IsSystemNamespace(Type type)
        {
            var ns = type?.Namespace;
            if (string.IsNullOrWhiteSpace(ns)) return false;
            if (type.DeclaringType is Type declaringType) return IsSystemNamespace(declaringType);
            return string.Equals(ns, "System", StringComparison.Ordinal) || ns.StartsWith("System.", StringComparison.Ordinal);
        }
    }
}
