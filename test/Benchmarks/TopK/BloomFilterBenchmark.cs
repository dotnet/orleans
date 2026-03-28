using System.Collections;
using System.Diagnostics;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Benchmarks.Serialization.Utilities;
using Orleans.Runtime.Placement.Repartitioning;

namespace Benchmarks.TopK;

[MemoryDiagnoser]
[FalsePositiveRateColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class BloomFilterBenchmark
{
    private BloomFilter _bloomFilter;
    private BloomFilter _bloomFilterWithSamples;
    private OriginalBloomFilter _originalBloomFilter;
    private OriginalBloomFilter _originalBloomFilterWithSamples;
    private BlockedBloomFilter _blockedBloomFilter;
    private BlockedBloomFilter _blockedBloomFilterWithSamples;
    private GrainId[] _population;
    private HashSet<GrainId> _set;
    private ZipfRejectionSampler _sampler;
    private GrainId[] _samples;

    [Params(1_000_000, Priority = 4)]
    public int Pop { get; set; }

    [Params(/*0.2, 0.4, 0.6, 0.8, */1.02 /*, 1.2, 1.4, 1.6*/, Priority = 3)]
    public double Skew { get; set; }

    [Params(1_000_000, Priority = 1)]
    public int Cap { get; set; }

    [Params(0.01, 0.001, Priority = 2)]
    public double FP { get; set; }

    [Params(10_000, Priority = 5)]
    public int Samples { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _population = new GrainId[Pop];
        _sampler = new(new Random(42), Pop, Skew);
        for (var i = 0; i < Pop; i++)
        {
            _population[i] = GrainId.Create($"grain_{i}", i.ToString());
        }

        _bloomFilter = new(Cap, FP);
        _bloomFilterWithSamples = new(Cap, FP);
        _originalBloomFilter = new();
        _originalBloomFilterWithSamples = new();
        _blockedBloomFilter = new(Cap, FP);
        _blockedBloomFilterWithSamples = new(Cap, FP);

        _samples = new GrainId[Samples];
        _set = new(Samples);
        for (var i = 0; i < Samples; i++)
        {
            //var sample = _sampler.Sample();
            var value = _population[i];
            _samples[i] = value;
            _set.Add(value);
            _bloomFilterWithSamples.Add(value);
            _originalBloomFilterWithSamples.Add(value);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Add")]
    public void BloomFilter_Add()
    {
        foreach (var sample in _samples)
        {
            _bloomFilter.Add(sample);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Contains")]
    public void BloomFilter_Contains()
    {
        foreach (var sample in _samples)
        {
            _bloomFilterWithSamples.Contains(sample);
        }
    }

    [Benchmark]
    [BenchmarkCategory("FP rate")]
    public int BloomFilter_FPR()
    {
        var correct = 0;
        var incorrect = 0;
        foreach (var sample in _population)
        {
            if (!_bloomFilterWithSamples.Contains(sample) == _set.Contains(sample))
            {
                correct++;
            }
            else
            {
                incorrect++;
            }
        }

        return incorrect;
    }


    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Add")]
    public void OriginalBloomFilter_Add()
    {
        foreach (var sample in _samples)
        {
            _originalBloomFilter.Add(sample);
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Contains")]
    public void OriginalBloomFilter_Contains()
    {
        foreach (var sample in _samples)
        {
            _originalBloomFilterWithSamples.Contains(sample);
        }
    }

    /*
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FP rate")]
    public int OriginalBloomFilter_FPR()
    {
        var correct = 0;
        var incorrect = 0;
        foreach (var sample in _population)
        {
            if (!_originalBloomFilterWithSamples.Contains(sample) == _set.Contains(sample))
            {
                correct++;
            }
            else
            {
                incorrect++;
            }
        }

        return incorrect;
    }
    */

    [Benchmark]
    [BenchmarkCategory("Add")]
    public void BlockedBloomFilter_Add()
    {
        foreach (var sample in _samples)
        {
            _blockedBloomFilter.Add(sample);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Contains")]
    public void BlockedBloomFilter_Contains()
    {
        foreach (var sample in _samples)
        {
            _blockedBloomFilterWithSamples.Contains(sample);
        }
    }

    // This is expected to yield a slighly higher FP rate, due to tuning
    [Benchmark]
    [BenchmarkCategory("FP rate")]
    public int BlockedBloomFilter_FPR()
    {
        var correct = 0;
        var incorrect = 0;
        foreach (var sample in _population)
        {
            if (!_blockedBloomFilterWithSamples.Contains(sample) == _set.Contains(sample))
            {
                correct++;
            }
            else
            {
                incorrect++;
            }
        }

        return incorrect;
    }
}

[AttributeUsage(AttributeTargets.Class)]
public class FalsePositiveRateColumnAttribute : Attribute, IConfigSource
{
    public FalsePositiveRateColumnAttribute(string columnName = "FP %")
    {
        var config = ManualConfig.CreateEmpty();
        config.AddColumn(
            new MethodResultColumn(columnName,
                val =>
                {
                    return $"{val}";
                }));
        Config = config;
    }

    public IConfig Config { get; }
}
public class OriginalBloomFilter
{
    private const int bitArraySize = 1_198_132; // formula 8 * n / ln(2) -> for 1000 elements, 0.01%
    private readonly int[] hashFuncSeeds = Enumerable.Range(0, 6).Select(p => (int)unchecked(p * 0xFBA4C795 + 1)).ToArray();
    private readonly BitArray filterBits = new(bitArraySize);

    public void Add(GrainId id)
    {
        foreach (int s in hashFuncSeeds)
        {
            uint i = XxHash32.HashToUInt32(id.Key.AsSpan(), s);
            filterBits.Set((int)(i % (uint)filterBits.Length), true);
        }
    }

    public bool Contains(GrainId id)
    {
        foreach (int s in hashFuncSeeds)
        {
            uint i = XxHash32.HashToUInt32(id.Key.AsSpan(), s);
            if (!filterBits.Get((int)(i % (uint)filterBits.Length)))
            {
                return false;
            }
        }
        return true;
    }
}

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

