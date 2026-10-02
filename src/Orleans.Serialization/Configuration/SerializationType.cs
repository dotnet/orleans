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

    /// <summary>
    /// Describes a concrete type or a generic type definition with its argument descriptions.
    /// </summary>
    /// <param name="type">The concrete type or generic definition.</param>
    /// <param name="arguments">The generic arguments, in declaration order.</param>
    /// <returns>The type description.</returns>
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
    /// Describes an array of a serialized type.
    /// </summary>
    /// <param name="element">The element type description.</param>
    /// <param name="rank">The array rank, starting at one for a vector.</param>
    /// <returns>The array description.</returns>
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
