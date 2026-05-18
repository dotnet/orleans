using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

internal readonly struct EquatableArray<T>(ImmutableArray<T> values) : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _values = StructuralEquality.Normalize(values);

    public static EquatableArray<T> Empty { get; } = new([]);

    public ImmutableArray<T> Values => StructuralEquality.Normalize(_values);

    public int Count => Values.Length;

    public int Length => Values.Length;

    public bool IsDefault => false;

    public bool IsEmpty => Values.IsEmpty;

    public bool IsDefaultOrEmpty => Values.IsEmpty;

    public T this[int index] => Values[index];

    public ImmutableArray<T>.Enumerator GetEnumerator() => Values.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => ((System.Collections.IEnumerable)Values).GetEnumerator();

    public bool Equals(EquatableArray<T> other) => StructuralEquality.SequenceEqual(Values, other.Values);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() => StructuralEquality.GetSequenceHashCode(Values);

    public static implicit operator EquatableArray<T>(ImmutableArray<T> values) => new(values);

    public static implicit operator ImmutableArray<T>(EquatableArray<T> values) => values.Values;

    public override string ToString() => Values.ToString();
}

internal static class StructuralEquality
{
    public static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values)
        => values.IsDefault ? [] : values;

    public static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right)
        => Normalize(left).SequenceEqual(Normalize(right));

    public static int GetSequenceHashCode<T>(ImmutableArray<T> values)
    {
        var normalizedValues = Normalize(values);
        var comparer = EqualityComparer<T>.Default;

        unchecked
        {
            var hash = 17;
            foreach (var item in normalizedValues)
            {
                hash = Combine(hash, item is null ? 0 : comparer.GetHashCode(item));
            }

            return hash;
        }
    }

    public static int GetHashCode(string? value)
        => StringComparer.Ordinal.GetHashCode(value ?? string.Empty);

    public static int Combine(int hash, int value)
    {
        unchecked
        {
            return hash * 31 + value;
        }
    }
}

internal sealed class ImmutableArrayComparer<T> : IEqualityComparer<ImmutableArray<T>>
{
    public static ImmutableArrayComparer<T> Instance { get; } = new();

    private ImmutableArrayComparer()
    {
    }

    public bool Equals(ImmutableArray<T> left, ImmutableArray<T> right)
        => StructuralEquality.SequenceEqual(left, right);

    public int GetHashCode(ImmutableArray<T> values)
        => StructuralEquality.GetSequenceHashCode(values);
}
