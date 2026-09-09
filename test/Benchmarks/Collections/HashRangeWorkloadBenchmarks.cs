using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using Entry = System.Collections.Generic.KeyValuePair<Orleans.Runtime.GrainId, int>;

namespace Benchmarks.Collections;

public enum HashRangeWorkload
{
    ReadHeavy,
    Mixed,
    HotMixed,
    Recovery,
    Churn
}

// Persistent workers keep thread creation outside measurement. OperationsPerInvoke counts the
// total work across all workers; each recovery batch includes exactly 32 range scans at every concurrency.
[ThreadingDiagnoser, OperationsPerSecond]
public class HashRangeWorkloadBenchmarks
{
    private const int Operations = 1_048_576;
    private const int RangeCadence = Operations / 32;
    private const int Population = 262_144;
    private Entry[] _entries = null!;
    private ConcurrentDictionary<GrainId, int> _concurrent = null!;
    private ConcurrentHashRangeDictionary<GrainId, int, GrainIdUniformHashComparer> _native = null!;
    private Barrier _barrier = null!;
    private long[] _results = null!;
    private Thread[] _workers = null!;
    private RingRange _range;
    private bool _useNative;
    private bool _stop;
    private int _round;
    private Exception? _workerError;

    [Params(1, 8, 32)]
    public int WorkerCount { get; set; }

    [ParamsAllValues]
    public HashRangeWorkload Workload { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _entries = HashRangeData.Create(Population);
        _concurrent = new();
        _native = new(default);
        foreach (var entry in _entries)
        {
            _concurrent.TryAdd(entry.Key, entry.Value);
            _native.TryAdd(entry.Key, entry.Value);
        }

        _range = RingRange.Create(0x80000000, 0x80000000 + uint.MaxValue / 1000);
        _barrier = new(WorkerCount + 1);
        _results = new long[WorkerCount];
        _workers = Enumerable.Range(0, WorkerCount)
            .Select(id => new Thread(() => Worker(id)) { IsBackground = true })
            .ToArray();
        foreach (var worker in _workers)
        {
            worker.Start();
        }

        try
        {
            var expected = ExpectedChecksum(round: 1);
            if (ConcurrentThroughput() != expected)
            {
                throw new InvalidOperationException("ConcurrentDictionary workload checksum differs from the model.");
            }

            _round = 0;
            if (NativeThroughput() != expected)
            {
                throw new InvalidOperationException("Native dictionary workload checksum differs from the model.");
            }

            HashRangeData.Validate(_entries, _concurrent);
            HashRangeData.Validate(_entries, _native);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    public long ConcurrentThroughput() => Run(native: false);

    [Benchmark(OperationsPerInvoke = Operations)]
    public long NativeThroughput() => Run(native: true);

    private long Run(bool native)
    {
        _useNative = native;
        _round = (_round + 1) & (Population - 1);
        _barrier.SignalAndWait();
        _barrier.SignalAndWait();
        if (_workerError is { } error)
        {
            throw new InvalidOperationException("Throughput worker failed.", error);
        }

        var result = 0L;
        foreach (var value in _results)
        {
            result += value;
        }

        return result;
    }

    private long Execute(int worker)
    {
        var checksum = 0L;
        var writesMask = Workload == HashRangeWorkload.ReadHeavy ? 31 : 3;
        var start = _round * 17;
        for (var i = 0; i < Operations / WorkerCount; i++)
        {
            var index = Workload == HashRangeWorkload.HotMixed
                ? (i * 17 + worker * 13) & 63
                : (start + i * WorkerCount + worker) & (Population - 1);
            var entry = _entries[index];
            if ((i & writesMask) == 0)
            {
                if (Workload == HashRangeWorkload.Churn)
                {
                    var removed = _useNative ? _native.TryRemove(entry) : _concurrent.TryRemove(entry);
                    var added = _useNative ? _native.TryAdd(entry.Key, entry.Value) : _concurrent.TryAdd(entry.Key, entry.Value);
                    if (!removed || !added)
                    {
                        throw new InvalidOperationException("An exclusively assigned lifecycle update failed.");
                    }
                }
                else if (_useNative)
                {
                    _native[entry.Key] = entry.Value;
                }
                else
                {
                    _concurrent[entry.Key] = entry.Value;
                }
            }
            else
            {
                var found = _useNative ? _native.TryGetValue(entry.Key, out var value) : _concurrent.TryGetValue(entry.Key, out value);
                if (!found || value != entry.Value)
                {
                    throw new InvalidOperationException("A published entry changed during an idempotent refresh workload.");
                }

                checksum += value;
            }

            if (Workload == HashRangeWorkload.Recovery && (i & (RangeCadence - 1)) == 0)
            {
                if (_useNative)
                {
                    foreach (var item in _native.EnumerateRange(_range))
                    {
                        checksum += item.Value;
                    }
                }
                else
                {
                    foreach (var item in _concurrent)
                    {
                        if (_range.Contains(item.Key.GetUniformHashCode()))
                        {
                            checksum += item.Value;
                        }
                    }
                }
            }
        }

        return checksum;
    }

    private long ExpectedChecksum(int round)
    {
        var result = 0L;
        var writesMask = Workload == HashRangeWorkload.ReadHeavy ? 31 : 3;
        for (var worker = 0; worker < WorkerCount; worker++)
        {
            for (var i = 0; i < Operations / WorkerCount; i++)
            {
                if ((i & writesMask) != 0)
                {
                    var index = Workload == HashRangeWorkload.HotMixed
                        ? (i * 17 + worker * 13) & 63
                        : (round * 17 + i * WorkerCount + worker) & (Population - 1);
                    result += _entries[index].Value;
                }
            }
        }

        if (Workload == HashRangeWorkload.Recovery)
        {
            result += (Operations / RangeCadence) * _entries.Where(entry => _range.Contains(entry.Key.GetUniformHashCode())).Sum(static entry => (long)entry.Value);
        }

        return result;
    }

    private void Worker(int id)
    {
        while (true)
        {
            _barrier.SignalAndWait();
            if (_stop)
            {
                return;
            }

            try
            {
                _results[id] = Execute(id);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _workerError, error, null);
            }
            finally
            {
                _barrier.SignalAndWait();
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_stop)
        {
            return;
        }

        _stop = true;
        _barrier.SignalAndWait();
        foreach (var worker in _workers)
        {
            worker.Join();
        }

        _barrier.Dispose();
    }
}
