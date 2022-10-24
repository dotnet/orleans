using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Orleans.Serialization.TypeSystem;

/// <summary>
/// Utility methods for formatting <see cref="Type"/> and <see cref="TypeInfo"/> instances in a way which can be parsed by
/// <see cref="Type.GetType(string)"/>.
/// </summary>
public static class RuntimeTypeNameFormatter
{
    private static readonly Assembly SystemAssembly = typeof(int).Assembly;

    private static readonly ConcurrentDictionary<Type, string> Cache = new ConcurrentDictionary<Type, string>();

    /// <summary>
    /// Returns a <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
    /// </summary>
    /// <param name="type">The type to format.</param>
    /// <returns>
    /// A <see cref="string"/> form of <paramref name="type"/> which can be parsed by <see cref="Type.GetType(string)"/>.
    /// </returns>
    public static string Format(Type type) => Cache.GetOrAdd(type, t =>
    {
        var builder = new StringBuilder();
        Format(builder, t, isElementType: false);
        return builder.ToString();
    });

    internal static string FormatInternalNoCache(Type type, bool allowAliases)
    {
        var builder = new StringBuilder();
        Format(builder, type, isElementType: false, allowAliases: allowAliases);
        return builder.ToString();
    }

    private static void Format(StringBuilder builder, Type type, bool isElementType, bool allowAliases = true)
    {
        if (allowAliases && type.GetCustomAttribute<CompoundTypeAliasAttribute>() is { } compoundAlias)
        {
            AddCompoundTypeAlias(builder, type, compoundAlias.Components);
            AddGenericParameters(builder, type);
            return;
        }

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
        if (!isElementType)
        {
            AddAssembly(builder, type);
        }
    }

    private static void AddCompoundTypeAlias(StringBuilder builder, Type type, object[] components)
    {
        // Start
        builder.Append('(');
        for (var i = 0; i < components.Length; ++i)
        {
            var component = components[i];
            if (i > 0)
            {
                builder.Append(',');
            }

            // Append the component
            if (component is string s)
            {
                builder.Append($"\"{s}\"");
            }
            else if (component is Type t)
            {
                if (t == type)
                {
                    throw new ArgumentException($"Element {i} of argument array, {component}, is equal to the attached type {type}, which is not supported");
                }

                builder.Append('[');
                Format(builder, t, isElementType: false);
                builder.Append(']');
            }
            else
            {
                throw new ArgumentException($"Element {i} of argument array, {component}, must be a Type or string but is an {component?.GetType().ToString() ?? "null"}");
            }
        }

        // End
        builder.Append(')');
        if (type.IsGenericType)
        {
            int parameterCount = type.IsConstructedGenericType switch
            {
                true => type.GenericTypeArguments.Length,
                false => type.GetTypeInfo().GenericTypeParameters.Length
            };
            builder.Append($"`{parameterCount}");
        }
    }

    private static void AddNamespace(StringBuilder builder, Type type)
    {
        if (string.IsNullOrWhiteSpace(type.Namespace))
        {
            return;
        }

        _ = builder.Append(type.Namespace);
        _ = builder.Append('.');
    }

    private static void AddClassName(StringBuilder builder, Type type)
    {
        // Format the declaring type.
        if (type.IsNested)
        {
            AddClassName(builder, type.DeclaringType);
            _ = builder.Append('+');
        }

        // Format the simple type name.
        var name = type.Name.AsSpan();
        var index = name.IndexOfAny("`*[&");
        _ = builder.Append(index > 0 ? name[..index] : name);

        // Format this type's generic arity.
        AddGenericArity(builder, type);
    }

    private static void AddGenericParameters(StringBuilder builder, Type type)
    {
        // Generic type definitions (eg, List<> without parameters) and non-generic types do not include any
        // parameters in their formatting.
        if (!type.IsConstructedGenericType)
        {
            return;
        }

        var args = type.GetGenericArguments();
        _ = builder.Append('[');
        for (var i = 0; i < args.Length; i++)
        {
            _ = builder.Append('[');
            Format(builder, args[i], isElementType: false);
            _ = builder.Append(']');
            if (i + 1 < args.Length)
            {
                _ = builder.Append(',');
            }
        }

        _ = builder.Append(']');
    }

    private static void AddGenericArity(StringBuilder builder, Type type)
    {
        if (!type.IsGenericType)
        {
            return;
        }

        // The arity is the number of generic parameters minus the number of generic parameters in the declaring types.
        var baseTypeParameterCount =
            type.IsNested ? type.DeclaringType.GetGenericArguments().Length : 0;
        var arity = type.GetGenericArguments().Length - baseTypeParameterCount;

        // If all of the generic parameters are in the declaring types then this type has no parameters of its own.
        if (arity == 0)
        {
            return;
        }

        _ = builder.Append('`');
        _ = builder.Append(arity);
    }

    private static void AddPointerSymbol(StringBuilder builder, Type type)
    {
        if (!type.IsPointer)
        {
            return;
        }

        _ = builder.Append('*');
    }

    private static void AddByRefSymbol(StringBuilder builder, Type type)
    {
        if (!type.IsByRef)
        {
            return;
        }

        _ = builder.Append('&');
    }

    private static void AddArrayRank(StringBuilder builder, Type type)
    {
        if (!type.IsArray)
        {
            return;
        }

        _ = builder.Append('[');
        _ = builder.Append(',', type.GetArrayRank() - 1);
        _ = builder.Append(']');
    }

    private static void AddAssembly(StringBuilder builder, Type type)
    {
        // Do not include the assembly name for the system assembly.
        var assembly = type.Assembly;
        if (SystemAssembly.Equals(assembly))
        {
            return;
        }

        _ = builder.Append(',');
        _ = builder.Append(CachedTypeResolver.GetName(assembly));
    }
}