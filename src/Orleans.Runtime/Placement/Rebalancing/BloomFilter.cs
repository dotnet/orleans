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
    private readonly int[] _hashFuncSeeds;
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

        var hashFuncCount = (int)Math.Min(minBitCount * 8 / capacity * Ln2 / 2, 8);
        Debug.Assert(hashFuncCount > 0);
        _hashFuncSeeds = Enumerable.Range(0, hashFuncCount).Select(p => (int)unchecked(p * 0xFBA4C795 + 1)).ToArray();
        Debug.Assert(_hashFuncSeeds.Length == hashFuncCount);
    }

    public void Add(GrainId id)
    {
        foreach (var seed in _hashFuncSeeds)
        {
            var indexes = XxHash3.HashToUInt64(id.Key.AsSpan(), (long)seed << 32 | id.GetUniformHashCode());
            Set((int)indexes);
            Set((int)(indexes >> 32));
        }
    }

    public bool Contains(GrainId id)
    {
        foreach (var seed in _hashFuncSeeds)
        {
            var indexes = XxHash3.HashToUInt64(id.Key.AsSpan(), (long)seed << 32 | id.GetUniformHashCode());
            var clear = IsClear((int)indexes);
            clear |= IsClear((int)(indexes >> 32));
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

    public void Reset() => Array.Clear(_filter);

    private static uint CeilingPowerOfTwo(uint x) => 1u << -BitOperations.LeadingZeroCount(x - 1);
}
