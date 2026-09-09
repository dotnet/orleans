using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;
using Entry = System.Collections.Generic.KeyValuePair<Orleans.Runtime.GrainId, int>;

namespace Benchmarks.Collections;

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class HashRangePointBenchmarks
{
    private Entry[] _entries = null!;
    private ConcurrentDictionary<GrainId, int> _concurrent = null!;
    private ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer> _native = null!;
    private Entry _hit;
    private GrainId _miss;
    private uint _hitHash;
    private uint _missHash;

    [Params(16_384, 262_144)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _entries = HashRangeData.Create(Count);
        _concurrent = (ConcurrentDictionary<GrainId, int>)ConcurrentConstructAndGrow(false);
        _native = (ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer>)NativeConstructAndGrow(false);
        _hit = _entries[_entries.Length / 2];
        _miss = GrainId.Create("hash-range-missing", "0");
        _hitHash = _hit.Key.GetUniformHashCode();
        _missHash = _miss.GetUniformHashCode();
        for (var i = 0; i < 2; i++)
        {
            if (ConcurrentHit() != _hit.Value || NativeHit() != _hit.Value || NativePrehashedHit() != _hit.Value
                || ConcurrentMiss() || NativeMiss() || NativePrehashedMiss()
                || !ConcurrentUpdateAndRestore() || !NativeUpdateAndRestore()
                || !ConcurrentRemoveAndRefill() || !NativeRemoveAndRefill())
            {
                throw new InvalidOperationException("Point operation or restoration failed.");
            }
        }

        HashRangeData.Validate(_entries, _concurrent);
        HashRangeData.Validate(_entries, _native);
        HashRangeData.Validate(_entries, (IEnumerable<Entry>)ConcurrentConstructAndGrow(true));
        HashRangeData.Validate(_entries, (IEnumerable<Entry>)NativeConstructAndGrow(true));
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Hit")]
    public int ConcurrentHit() => _concurrent.TryGetValue(_hit.Key, out var value) ? value : -1;

    [Benchmark, BenchmarkCategory("Hit")]
    public int NativeHit() => _native.TryGetValue(_hit.Key, out var value) ? value : -1;

    [Benchmark, BenchmarkCategory("Hit")]
    public int NativePrehashedHit() => _native.TryGetValue(_hit.Key, _hitHash, out var value) ? value : -1;

    [Benchmark(Baseline = true), BenchmarkCategory("Miss")]
    public bool ConcurrentMiss() => _concurrent.TryGetValue(_miss, out _);

    [Benchmark, BenchmarkCategory("Miss")]
    public bool NativeMiss() => _native.TryGetValue(_miss, out _);

    [Benchmark, BenchmarkCategory("Miss")]
    public bool NativePrehashedMiss() => _native.TryGetValue(_miss, _missHash, out _);

    [Benchmark(Baseline = true), BenchmarkCategory("UpdateAndRestore")]
    public bool ConcurrentUpdateAndRestore()
        => _concurrent.TryUpdate(_hit.Key, -1, _hit.Value) & _concurrent.TryUpdate(_hit.Key, _hit.Value, -1);

    [Benchmark, BenchmarkCategory("UpdateAndRestore")]
    public bool NativeUpdateAndRestore()
        => _native.TryUpdate(_hit.Key, -1, _hit.Value) & _native.TryUpdate(_hit.Key, _hit.Value, -1);

    [Benchmark(Baseline = true), BenchmarkCategory("ConditionalRemoveAndRefill")]
    public bool ConcurrentRemoveAndRefill()
        => ((ICollection<Entry>)_concurrent).Remove(_hit) & _concurrent.TryAdd(_hit.Key, _hit.Value);

    [Benchmark, BenchmarkCategory("ConditionalRemoveAndRefill")]
    public bool NativeRemoveAndRefill()
        => _native.TryRemove(_hit) & _native.TryAdd(_hit.Key, _hit.Value);

    // These measure cumulative construction/growth allocation, not the retained size of a populated collection.
    [Benchmark(Baseline = true), BenchmarkCategory("ConstructAndGrow")]
    [Arguments(false), Arguments(true)]
    public object ConcurrentConstructAndGrow(bool presize)
    {
        var result = new ConcurrentDictionary<GrainId, int>(Environment.ProcessorCount, presize ? Count : 32);
        foreach (var entry in _entries)
        {
            result.TryAdd(entry.Key, entry.Value);
        }

        return result;
    }

    [Benchmark, BenchmarkCategory("ConstructAndGrow")]
    [Arguments(false), Arguments(true)]
    public object NativeConstructAndGrow(bool presize)
    {
        var result = new ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer>(
            GrainIdUniformHashComparer.Instance, capacity: presize ? Count : 32, concurrencyLevel: Environment.ProcessorCount);
        foreach (var entry in _entries)
        {
            result.TryAdd(entry.Key, entry.Value);
        }

        return result;
    }
}
