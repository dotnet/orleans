using System.Collections;
using System.IO.Hashing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using Benchmarks.Utilities;
using Orleans.Runtime.Placement.Rebalancing;

namespace Benchmarks.TopK;

[MemoryDiagnoser]
[FalsePositiveRateColumn]
public class BloomFilterBenchmark
{
    private BloomFilter _bloomFilter;
    private BloomFilter _bloomFilterWithSamples;
    private OriginalBloomFilter _originalBloomFilter;
    private OriginalBloomFilter _originalBloomFilterWithSamples;
    private GrainId[] _population;
    private HashSet<GrainId> _set;
    private ZipfRejectionSampler _sampler;
    private GrainId[] _samples;

    [Params(1_000_000, Priority = 3)]
    public int Pop { get; set; }

    [Params(/*0.2, 0.4, 0.6, 0.8, */1.02 /*, 1.2, 1.4, 1.6*/, Priority = 2)]
    public double Skew { get; set; }

    [Params(1_000_000, Priority = 1)]
    public int Cap { get; set; }

    [Params(10_000, Priority = 4)]
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

        _bloomFilter = new(Cap, 0.01);
        _bloomFilterWithSamples = new(Cap, 0.01);
        _originalBloomFilter = new();
        _originalBloomFilterWithSamples = new();

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

    /*
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
    */

    [Benchmark]
    [BenchmarkCategory("Add")]
    public void OriginalBloomFilter_Add()
    {
        foreach (var sample in _samples)
        {
            _originalBloomFilter.Add(sample);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Contains")]
    public void OriginalBloomFilter_Contains()
    {
        foreach (var sample in _samples)
        {
            _originalBloomFilterWithSamples.Contains(sample);
        }
    }

    /*
    [Benchmark]
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
