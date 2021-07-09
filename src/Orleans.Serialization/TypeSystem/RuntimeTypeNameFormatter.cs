using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Utility methods for formatting <see cref="Type"/> and <see cref="TypeInfo"/> instances in a way which can be parsed by
    /// <see cref="Type.GetType(string)"/>.
    /// </summary>
    public static class RuntimeTypeNameFormatter
    {
        private static readonly Assembly SystemAssembly = typeof(int).GetTypeInfo().Assembly;
        private static readonly char[] SimpleNameTerminators = { '`', '*', '[', '&' };

        private static readonly ConcurrentDictionary<Type, string> Cache = new ConcurrentDictionary<Type, string>();

        /// <summary>
        /// Returns a <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
        /// </summary>
        /// <param name="type">The type to format.</param>
        /// <returns>
        /// A <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
        /// </returns>
        public static string Format(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (!Cache.TryGetValue(type, out var result))
            {
                static string FormatType(Type t)
                {
                    var builder = new StringBuilder();
                    Format(builder, t.GetTypeInfo(), isElementType: false);
                    return builder.ToString();
                }

                result = Cache.GetOrAdd(type, FormatType);
            }

            return result;
        }

        private static void Format(StringBuilder builder, TypeInfo type, bool isElementType)
        {
            // Arrays, pointers, and by-ref types are all element types and need to be formatted with their own adornments.
            if (type.HasElementType)
            {
                // Format the element type.
                Format(builder, type.GetElementType().GetTypeInfo(), isElementType: true);

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
            if (!isElementType)
            {
                AddAssembly(builder, type);
            }
        }

        private static void AddNamespace(StringBuilder builder, TypeInfo type)
        {
            if (string.IsNullOrWhiteSpace(type.Namespace))
            {
                return;
            }

            _ = builder.Append(type.Namespace);
            _ = builder.Append('.');
        }

        private static void AddClassName(StringBuilder builder, TypeInfo type)
        {
            // Format the declaring type.
            if (type.IsNested)
            {
                AddClassName(builder, type.DeclaringType.GetTypeInfo());
                _ = builder.Append('+');
            }

            // Format the simple type name.
            var index = type.Name.IndexOfAny(SimpleNameTerminators);
            _ = builder.Append(index > 0 ? type.Name.Substring(0, index) : type.Name);

            // Format this type's generic arity.
            AddGenericArity(builder, type);
        }

        private static void AddGenericParameters(StringBuilder builder, TypeInfo type)
        {
            // Generic type definitions (eg, List<> without parameters) and non-generic types do not include any
            // parameters in their formatting.
            if (!type.AsType().IsConstructedGenericType)
            {
                return;
            }

            var args = type.GetGenericArguments();
            _ = builder.Append('[');
            for (var i = 0; i < args.Length; i++)
            {
                _ = builder.Append('[');
                Format(builder, args[i].GetTypeInfo(), isElementType: false);
                _ = builder.Append(']');
                if (i + 1 < args.Length)
                {
                    _ = builder.Append(',');
                }
            }

            _ = builder.Append(']');
        }

        private static void AddGenericArity(StringBuilder builder, TypeInfo type)
        {
            if (!type.IsGenericType)
            {
                return;
            }

            // The arity is the number of generic parameters minus the number of generic parameters in the declaring types.
            var baseTypeParameterCount =
                type.IsNested ? type.DeclaringType.GetTypeInfo().GetGenericArguments().Length : 0;
            var arity = type.GetGenericArguments().Length - baseTypeParameterCount;

            // If all of the generic parameters are in the declaring types then this type has no parameters of its own.
            if (arity == 0)
            {
                return;
            }

            _ = builder.Append('`');
            _ = builder.Append(arity);
        }

        private static void AddPointerSymbol(StringBuilder builder, TypeInfo type)
        {
            if (!type.IsPointer)
            {
                return;
            }

            _ = builder.Append('*');
        }

        private static void AddByRefSymbol(StringBuilder builder, TypeInfo type)
        {
            if (!type.IsByRef)
            {
                return;
            }

            _ = builder.Append('&');
        }

        private static void AddArrayRank(StringBuilder builder, TypeInfo type)
        {
            if (!type.IsArray)
            {
                return;
            }

            _ = builder.Append('[');
            _ = builder.Append(',', type.GetArrayRank() - 1);
            _ = builder.Append(']');
        }

        private static void AddAssembly(StringBuilder builder, TypeInfo type)
        {
            // Do not include the assembly name for the system assembly.
            if (SystemAssembly.Equals(type.Assembly))
            {
                return;
            }

            _ = builder.Append(',');
            _ = builder.Append(type.Assembly.GetName().Name);
        }
    }
}