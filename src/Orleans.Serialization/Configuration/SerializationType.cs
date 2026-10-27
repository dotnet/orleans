using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace Orleans.Serialization.Configuration;

/// <summary>
/// Describes a serialization type using concrete types and an implementation's generic parameters.
/// </summary>
public sealed class SerializationType
{
    private SerializationType(Type? type, int parameterIndex, int arrayRank, SerializationType[] arguments)
    {
        Type = type;
        ParameterIndex = parameterIndex;
        ArrayRank = arrayRank;
        Arguments = arguments;
    }

    internal Type? Type { get; }
    internal int ParameterIndex { get; }
    internal int ArrayRank { get; }
    internal SerializationType[] Arguments { get; }

    internal static bool AreEquivalent(SerializationType? left, SerializationType? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Type != right.Type || left.ParameterIndex != right.ParameterIndex
            || left.ArrayRank != right.ArrayRank || left.Arguments.Length != right.Arguments.Length)
        {
            return false;
        }
        for (var i = 0; i < left.Arguments.Length; i++)
        {
            if (!AreEquivalent(left.Arguments[i], right.Arguments[i]))
            {
                return false;
            }
        }
        return true;
    }

    internal static SerializationType FromType(Type type, Type[] implementationParameters)
    {
        if (type.IsGenericParameter)
        {
            var index = System.Array.IndexOf(implementationParameters, type);
            if (index < 0)
            {
                throw new InvalidOperationException($"Legacy serialization target parameter {type} is not declared by its implementation.");
            }
            return Parameter(index);
        }
        if (type.IsArray && type.ContainsGenericParameters)
        {
            return Array(FromType(type.GetElementType()!, implementationParameters), type.GetArrayRank());
        }
        if (type.IsGenericType && type.ContainsGenericParameters)
        {
            var arguments = type.GetGenericArguments();
            var descriptions = new SerializationType[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                descriptions[i] = FromType(arguments[i], implementationParameters);
            }
            return new(type.GetGenericTypeDefinition(), -1, 0, descriptions);
        }
        return new(type, -1, 0, System.Array.Empty<SerializationType>());
    }

    /// <summary>
    /// Describes a concrete type or a generic type definition with its argument descriptions.
    /// </summary>
    /// <param name="type">The concrete type or generic definition.</param>
    /// <param name="arguments">The generic arguments, in declaration order.</param>
    /// <returns>The type description.</returns>
    /// <remarks>
    /// Generic definitions supplied without argument descriptions use the implementation's
    /// generic parameters in declaration order for target matching and executable type resolution.
    /// Supply argument descriptions to bind a subset of the parameters or reorder them.
    /// Array types supplied to this method must be closed. Use <see cref="Array"/>
    /// to describe a structural target-matching pattern with generic element parameters.
    /// </remarks>
    public static SerializationType Create(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
        Type type,
        params SerializationType[] arguments)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (arguments is null)
        {
            throw new ArgumentNullException(nameof(arguments));
        }

        if (type.IsArray && type.ContainsGenericParameters)
        {
            throw new ArgumentException(
                "An executable array type must be closed. Use SerializationType.Array to describe a structural target-matching pattern.",
                nameof(type));
        }

        if (arguments.Length > 0 && (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != arguments.Length))
        {
            throw new ArgumentException("The generic argument descriptions must match the type definition's arity.", nameof(arguments));
        }

        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("Generic argument descriptions must be non-null.", nameof(arguments));
            }
        }

        return new(type, -1, 0, (SerializationType[])arguments.Clone());
    }

    /// <summary>
    /// Describes an implementation's generic parameter.
    /// </summary>
    /// <param name="index">The zero-based parameter index, including parameters of declaring types.</param>
    /// <returns>The parameter description.</returns>
    public static SerializationType Parameter(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new(null, index, 0, System.Array.Empty<SerializationType>());
    }

    /// <summary>
    /// Describes an array shape for matching a serialization contract's target.
    /// </summary>
    /// <param name="element">The element type description.</param>
    /// <param name="rank">The array rank, starting at one for a vector.</param>
    /// <returns>The array description.</returns>
    /// <remarks>
    /// Array descriptions bind element parameters when matching a requested target type.
    /// Executable type resolution requires a source-known closed array supplied using
    /// <see cref="Create"/> or a closed converter registration. Resolving an array shape
    /// as an executable type throws <see cref="NotSupportedException"/> on all runtimes.
    /// </remarks>
    public static SerializationType Array(SerializationType element, int rank = 1)
    {
        if (element is null)
        {
            throw new ArgumentNullException(nameof(element));
        }

        if (rank < 1 || rank > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        return new(null, -1, rank, [element]);
    }

    internal IEnumerable<Type> GetReferencedTypes()
    {
        if (Type is { } type)
        {
            yield return type;
        }

        foreach (var argument in Arguments)
        {
            foreach (var referenced in argument.GetReferencedTypes())
            {
                yield return referenced;
            }
        }
    }
}
