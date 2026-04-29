using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

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
