using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using Entry = System.Collections.Generic.KeyValuePair<Orleans.Runtime.GrainId, int>;

namespace Benchmarks.Collections;

public enum HashRangeScenario
{
    Empty, Tiny, Sparse, Moderate, Full, Wrapped, Skewed, Colliding, TinyLarge, SparseLarge, FullLarge
}

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class HashRangeScanBenchmarks
{
    private ConcurrentDictionary<GrainId, int> _concurrent = null!;
    private ConcurrentHashRangeDictionary<GrainId, int, ScenarioComparer> _native = null!;
    private ScenarioComparer _comparer;
    private RingRange _range;

    // Bounded profiles keep the expensive large/collision populations focused on their distinct behaviors.
    [ParamsAllValues]
    public HashRangeScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Bound the deliberately quadratic collision lifecycle; ordinary profiles use the same larger population.
        var entries = HashRangeData.Create(Scenario switch
        {
            HashRangeScenario.Colliding => 1_024,
            HashRangeScenario.TinyLarge or HashRangeScenario.SparseLarge or HashRangeScenario.FullLarge => 262_144,
            _ => 16_384
        });
        _comparer = new(Scenario);
        _range = Scenario switch
        {
            HashRangeScenario.Empty => RingRange.Empty,
            HashRangeScenario.Tiny or HashRangeScenario.TinyLarge => RingRange.FromPoint(Hash(entries[0].Key)),
            HashRangeScenario.Full or HashRangeScenario.FullLarge => RingRange.Full,
            HashRangeScenario.Wrapped => RingRange.Create(0xf0000000, 0x0fffffff),
            HashRangeScenario.Moderate => RingRange.Create(0x80000000, 0x9fffffff),
            HashRangeScenario.Colliding => RingRange.FromPoint(0x80000001),
            _ => RingRange.Create(0x80000000, 0x80000000 + uint.MaxValue / 1000)
        };
        _concurrent = new();
        _native = new(_comparer);
        foreach (var entry in entries)
        {
            _concurrent.TryAdd(entry.Key, entry.Value);
            _native.TryAdd(entry.Key, entry.Value);
        }

        var expected = entries.Where(entry => _range.Contains(Hash(entry.Key))).ToArray();
        HashRangeData.Validate(expected, ConcurrentMaterialize());
        HashRangeData.Validate(expected, NativeMaterialize());
        if (ConcurrentScan() != NativeScan())
        {
            throw new InvalidOperationException("Range scan checksums differ.");
        }
        for (var i = 0; i < 2; i++)
        {
            HashRangeData.Validate(expected, ConcurrentRemoveAndRefill());
            HashRangeData.Validate(expected, NativeRemoveAndRefill());
            HashRangeData.Validate(entries, _concurrent);
            HashRangeData.Validate(entries, _native);
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("MapMaterialize")]
    public Entry[] ConcurrentMaterialize() => _concurrent.Where(entry => _range.Contains(Hash(entry.Key))).ToArray();

    [Benchmark, BenchmarkCategory("MapMaterialize")]
    public Entry[] NativeMaterialize() => _native.EnumerateRange(_range).ToArray();

    [Benchmark(Baseline = true), BenchmarkCategory("MapScan")]
    public long ConcurrentScan()
    {
        var result = 0L;
        foreach (var entry in _concurrent)
        {
            if (_range.Contains(Hash(entry.Key)))
            {
                result += entry.Value;
            }
        }

        return result;
    }

    [Benchmark, BenchmarkCategory("MapScan")]
    public long NativeScan()
    {
        var result = 0L;
        foreach (var entry in _native.EnumerateRange(_range))
        {
            result += entry.Value;
        }

        return result;
    }

    // Both lifecycles include candidate materialization, conditional removal, result allocation and refill.
    [Benchmark(Baseline = true), BenchmarkCategory("RangeRemoveAndRefill")]
    public List<Entry> ConcurrentRemoveAndRefill()
    {
        var candidates = _concurrent.Where(entry => _range.Contains(Hash(entry.Key))).ToList();
        var removed = new List<Entry>(candidates.Count);
        foreach (var entry in candidates)
        {
            if (((ICollection<Entry>)_concurrent).Remove(entry))
            {
                removed.Add(entry);
            }
        }

        foreach (var entry in removed)
        {
            _concurrent.TryAdd(entry.Key, entry.Value);
        }

        return removed;
    }

    [Benchmark, BenchmarkCategory("RangeRemoveAndRefill")]
    public List<Entry> NativeRemoveAndRefill()
    {
        var removed = new List<Entry>();
        _native.RemoveRange(_range, removed);
        foreach (var entry in removed)
        {
            _native.TryAdd(entry.Key, entry.Value);
        }

        return removed;
    }

    private uint Hash(GrainId key) => _comparer.GetHashCode(key);

    private readonly struct ScenarioComparer(HashRangeScenario scenario) : IConsistentHashComparer<GrainId>
    {
        public bool Equals(GrainId x, GrainId y) => x.Equals(y);

        public uint GetHashCode(GrainId value) => scenario switch
        {
            HashRangeScenario.Skewed => 0x80000000 + (value.GetUniformHashCode() >> 8),
            HashRangeScenario.Colliding => 0x80000001,
            _ => value.GetUniformHashCode()
        };
    }
}

internal static class HashRangeData
{
    internal static Entry[] Create(int count)
    {
        var random = new Random(7451);
        return Enumerable.Range(0, count)
            .Select(i => new Entry(GrainId.Create("hash-range", $"{i:x8}-{random.NextInt64():x16}"), i + 1)).ToArray();
    }

    internal static void Validate(IEnumerable<Entry> expected, IEnumerable<Entry> actual)
    {
        if (!expected.OrderBy(entry => entry.Value).SequenceEqual(actual.OrderBy(entry => entry.Value)))
        {
            throw new InvalidOperationException("Hash-range results or restored population differ.");
        }
    }
}
