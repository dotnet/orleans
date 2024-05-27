using System;
using System.Diagnostics;
using System.IO.Hashing;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed class BloomFilter
{
    private const double Ln2Squared = 0.4804530139182014246671025263266649717305529515945455;
    private const double Ln2 = 0.6931471805599453094172321214581765680755001343602552;
    private readonly ulong[] _hashFuncSeeds;
    private readonly int[] _filter;
    private readonly int _indexMask;

    public BloomFilter(int capacity, double falsePositiveRate)
    {
        // Calculate the ideal bloom filter size and hash code count for the given (estimated) capacity and desired false positive rate.
        // See https://en.wikipedia.org/wiki/Bloom_filter.
        var minBitCount = (int)(-1 / Ln2Squared * capacity * Math.Log(falsePositiveRate)) / 8;
        var arraySize = (int)CeilingPowerOfTwo((uint)(minBitCount - 1 + (1 << 5)) >> 5);
        _indexMask = arraySize - 1;
        _filter = new int[arraySize];

        // Divide the hash count by 2 since we are using 64-bit hash codes split into two 32-bit hash codes.
        var hashFuncCount = (int)Math.Min(minBitCount * 8 / capacity * Ln2 / 2, 8);
        Debug.Assert(hashFuncCount > 0);
        _hashFuncSeeds = Enumerable.Range(0, hashFuncCount).Select(p => unchecked((ulong)p * 0xFBA4C795FBA4C795 + 1)).ToArray();
        Debug.Assert(_hashFuncSeeds.Length == hashFuncCount);
    }

    public void Add(GrainId id)
    {
        var hash = XxHash3.HashToUInt64(id.Key.AsSpan(), id.GetUniformHashCode());
        foreach (var seed in _hashFuncSeeds)
        {
            hash = Mix64(hash ^ seed);
            Set((int)hash);
            Set((int)(hash >> 32));
        }
    }

    public bool Contains(GrainId id)
    {
        var hash = XxHash3.HashToUInt64(id.Key.AsSpan(), id.GetUniformHashCode());
        foreach (var seed in _hashFuncSeeds)
        {
            hash = Mix64(hash ^ seed);
            var clear = IsClear((int)hash);
            clear |= IsClear((int)(hash >> 32));
            if (clear)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsClear(int index) => (_filter[(index >> 5) & _indexMask] & (1 << index)) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index) => _filter[(index >> 5) & _indexMask] |= 1 << index;

    /// <summary>
    /// Computes Stafford variant 13 of 64-bit mix function.
    /// </summary>
    /// <param name="z">The input parameter.</param>
    /// <returns>A bit mix of the input parameter.</returns>
    /// <remarks>
    /// See http://zimbry.blogspot.com/2011/09/better-bit-mixing-improving-on.html
    /// </remarks>
    public static ulong Mix64(ulong z)
    {
        z = (z ^ z >> 30) * 0xbf58476d1ce4e5b9L;
        z = (z ^ z >> 27) * 0x94d049bb133111ebL;
        return z ^ z >> 31;
    }

    public void Reset() => Array.Clear(_filter);

    private static uint CeilingPowerOfTwo(uint x) => 1u << -BitOperations.LeadingZeroCount(x - 1);
}
