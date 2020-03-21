using System;
using System.Collections.Immutable;

namespace Orleans.Runtime.Utilities
{
    internal static class ImmutableCollectionExtensions
    {
        public static int FindIndex<T>(this ImmutableArray<T> collection, Func<T, bool> predicate)
        {
            for(var index=0; index <collection.Length; index++)
            {
                if (predicate(collection[index])) return index;
            }

            return -1;
        }

        public static int FindLastIndex<T>(this ImmutableArray<T> collection, Func<T, bool> predicate)
        {
            for (var index = collection.Length - 1; index >= 0; --index)
            {
                if (predicate(collection[index])) return index;
            }

            return -1;
        }
    }
}
