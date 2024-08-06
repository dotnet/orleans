using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Utilities;

internal static class SearchAlgorithms
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BinarySearch<TState>(int length, TState state, Func<int, TState, int> comparer)
    {
        var left = 0;
        var right = length - 1;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var comparison = comparer(mid, state);

            if (comparison == 0)
            {
                return mid;
            }
            else if (comparison < 0)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return -1;
    }
}
