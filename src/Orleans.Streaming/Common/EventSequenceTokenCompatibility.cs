using System;
using System.Collections.Concurrent;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common;

internal static class EventSequenceTokenCompatibility
{
    private static readonly ConcurrentDictionary<Type, TokenContract> Contracts = new();

    public static bool IsCompatibleNumericToken(StreamSequenceToken left, StreamSequenceToken right)
        => left.GetType() == right.GetType() || HasNumericContract(left) && HasNumericContract(right);

    public static bool HasInheritedContract(StreamSequenceToken token, Type contractType, Type objectEqualsType)
    {
        // Resolve virtual and interface dispatch once per type, including explicit interface reimplementations.
        var contract = Contracts.GetOrAdd(token.GetType(), static (_, value) => new TokenContract(
            new Func<object?, bool>(value.Equals).Method.DeclaringType,
            new Func<StreamSequenceToken?, bool>(value.Equals).Method.DeclaringType,
            new Func<StreamSequenceToken?, int>(value.CompareTo).Method.DeclaringType,
            new Func<int>(value.GetHashCode).Method.DeclaringType,
            new Func<StreamSequenceToken?, bool>(((IEquatable<StreamSequenceToken?>)value).Equals).Method.DeclaringType,
            new Func<StreamSequenceToken?, int>(((IComparable<StreamSequenceToken?>)value).CompareTo).Method.DeclaringType), token);

        return contract.ObjectEquals == objectEqualsType
            && contract.TokenEquals == contractType
            && contract.CompareTo == contractType
            && contract.Hashing == contractType
            && contract.EquatableEquals == contractType
            && contract.ComparableCompareTo == contractType;
    }

    public static int Compare(StreamSequenceToken left, StreamSequenceToken right)
    {
        Normalize(ref left, ref right);
        return left.CompareTo(right);
    }

    public static bool AreEqual(StreamSequenceToken left, StreamSequenceToken right)
    {
        Normalize(ref left, ref right);
        return left.Equals(right);
    }

    private static bool HasNumericContract(StreamSequenceToken token)
        => token switch
        {
            EventSequenceToken => HasInheritedContract(token, typeof(EventSequenceToken), typeof(EventSequenceToken)),
            EventSequenceTokenV2 => HasInheritedContract(token, typeof(EventSequenceTokenV2), typeof(EventSequenceTokenV2)),
            _ => false,
        };

    private static void Normalize(ref StreamSequenceToken left, ref StreamSequenceToken right)
    {
        // Provider-specific legacy positions are normalized only for recovery comparisons.
        // Public token equality keeps the provider's contract, and delivered tokens keep their metadata.
        if (left is EventSequenceToken leftProvider)
        {
            right = leftProvider.NormalizeLegacyToken(right);
        }

        if (right is EventSequenceToken rightProvider)
        {
            left = rightProvider.NormalizeLegacyToken(left);
        }
    }

    private sealed record TokenContract(
        Type? ObjectEquals,
        Type? TokenEquals,
        Type? CompareTo,
        Type? Hashing,
        Type? EquatableEquals,
        Type? ComparableCompareTo);
}
