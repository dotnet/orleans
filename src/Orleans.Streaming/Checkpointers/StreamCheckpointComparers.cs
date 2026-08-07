using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Orleans.Streams
{
    /// <summary>
    /// Common comparers for stream checkpoint values.
    /// </summary>
    public static class StreamCheckpointComparers
    {
        /// <summary>
        /// Gets a comparer for integer checkpoint values of arbitrary size.
        /// </summary>
        /// <remarks>
        /// If either value is not an integer, the values compare equal so that an invalid checkpoint cannot advance.
        /// </remarks>
        public static IComparer<string> Numeric { get; } = Comparer<string>.Create(static (left, right) =>
        {
            return BigInteger.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftValue)
                && BigInteger.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightValue)
                    ? leftValue.CompareTo(rightValue)
                    : 0;
        });
    }
}
