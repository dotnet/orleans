using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
{
    internal static class ImmutableArrayValueComparer
    {
        public static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values)
            => values.IsDefault ? ImmutableArray<T>.Empty : values;

        public static bool Equals<T>(ImmutableArray<T> left, ImmutableArray<T> right)
        {
            var normalizedLeft = Normalize(left);
            var normalizedRight = Normalize(right);

            if (normalizedLeft.Length != normalizedRight.Length)
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < normalizedLeft.Length; i++)
            {
                if (!comparer.Equals(normalizedLeft[i], normalizedRight[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public static int GetHashCode<T>(ImmutableArray<T> values)
        {
            var normalizedValues = Normalize(values);
            var comparer = EqualityComparer<T>.Default;

            unchecked
            {
                var hash = 17;
                foreach (var item in normalizedValues)
                {
                    hash = hash * 31 + (item is null ? 0 : comparer.GetHashCode(item));
                }

                return hash;
            }
        }
    }
}
