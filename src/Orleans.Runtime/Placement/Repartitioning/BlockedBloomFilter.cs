using System;
using System.Diagnostics;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Placement.Repartitioning;

/// <summary>
/// A tuned version of a blocked bloom filter implementation.
/// </summary>
/// <remarks><i>
/// <para>This is a tuned version of BBF in order to meet the required FP rate.
/// Tuning takes a lot of time so this filter can accept FP rates in the rage of [0.1% - 1%]
/// Any value with the range, at any precision is supported as the FP rate is regressed via polynomial regression</para>
/// <para>More information can be read from Section 3: https://www.cs.amherst.edu/~ccmcgeoch/cs34/papers/cacheefficientbloomfilters-jea.pdf</para>
/// </i></remarks>
internal sealed class BlockedBloomFilter
{
    private const int BlockSize = 32; // higher value yields better speed, but at a high cost of space
    private const double Ln2Squared = 0.4804530139182014246671025263266649717305529515945455;
    private const double MinFpRate = 0.001; // 0.1%
    private const double MaxFpRate = 0.01;  // 1%

    private readonly int _blocks;
    private readonly int[] _filter;

    // Regression coefficients (derived via polynomial regression) to match 'fpRate' as the actual deviates significantly with lower and lower 'fpRate'
    // Eg, see https://gist.github.com/ledjon-behluli/d339cbd54568ceb5464d3a947ac8f08e
    private static readonly double[] Coefficients =
    [
         4.0102253166524500e-003,
        -1.6272682781603145e+001,
         2.7169897602930665e+004,
        -2.4527698904812500e+007,
         1.3273846004698063e+010,
        -4.4943809759769805e+012,
         9.5588839677303638e+014,
        -1.2081452101930328e+017,
         6.8958853188430172e+018,
         2.6889929911921561e+020,
        -7.1061179529975569e+022,
         4.4109449793357217e+024,
        -9.8041203512310751e+025
    ];

    /// <param name="capacity">The estimated population size.</param>
    /// <param name="fpRate">Bounded within [<see cref="MinFpRate"/> - <see cref="MaxFpRate"/>]</param>
    /// <exception cref="ArgumentOutOfRangeException"/>
    public BlockedBloomFilter(int capacity, double fpRate)
    {
        if (fpRate is < MinFpRate or > MaxFpRate)
        {
            throw new ArgumentOutOfRangeException($"False positive rate '{fpRate}', is outside of the allowed range '{MinFpRate} - {MaxFpRate}'");
        }

        var adjFpRate = RegressFpRate(fpRate);
        Debug.Assert(adjFpRate < fpRate);
        var bits = (int)(-1 * capacity * Math.Log(adjFpRate) / Ln2Squared);

        _blocks = bits / BlockSize;
        _filter = new int[_blocks + 1];
    }

    private static double RegressFpRate(double fpRate)
    {
        double temp = 1;
        double result = 0;

        foreach (var coefficient in Coefficients)
        {
            result += coefficient * temp;
            temp *= fpRate;
        }

        return Math.Abs(result);
    }

    public void Add(GrainId id)
    {
        var hash = XxHash3.HashToUInt64(id.Key.AsSpan(), id.GetUniformHashCode());
        var index = GetBlockIndex(hash, _blocks); // important to get index before rotating the hash

        hash ^= BitOperations.RotateLeft(hash, 32);

        // We use 2 masks to distribute the bits of the hash value across multiple positions in the filter
        var mask1 = ComputeMask1(hash);
        var mask2 = ComputeMask2(hash);

        // We set the bits across 2 blocks so that the bits from a single hash value, are spread out more evenly across the filter.
        _filter[index] |= mask1;
        _filter[index + 1] |= mask2;
    }

    public bool Contains(GrainId id)
    {
        var hash = XxHash3.HashToUInt64(id.Key.AsSpan(), id.GetUniformHashCode());
        var index = GetBlockIndex(hash, _blocks); // important to get index before rotating the hash

        hash ^= BitOperations.RotateLeft(hash, 32);

        var block1 = _filter[index];
        var block2 = _filter[index + 1];

        var mask1 = ComputeMask1(hash);
        var mask2 = ComputeMask2(hash);

        return (mask1 & block1) == mask1 && (mask2 & block2) == mask2;
    }

    public void Reset() => Array.Clear(_filter);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBlockIndex(ulong hash, int buckets) => (int)(((int)hash & 0xffffffffL) * buckets >> 32);

    /// <summary>
    /// Sets the bits of <paramref name="hash"/> corresponding to the lower-order bits, and the bits shifted by 6 positions to the right
    /// </summary>
    /// <param name="hash">The rotated hash</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeMask1(ulong hash) => (1 << (int)hash) | (1 << ((int)hash >> 6));

    /// <summary>
    /// Sets the bits of <paramref name="hash"/>, and the bits shifted by 12 and 18 positions to the right
    /// </summary>
    /// <param name="hash">The rotated hash</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeMask2(ulong hash) => (1 << ((int)hash >> 12)) | (1 << ((int)hash >> 18));
}