using BenchmarkDotNet.Attributes;
using Orleans.Caching;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;

namespace Benchmarks.Caching;

[MemoryDiagnoser]
[BenchmarkCategory("Caching")]
public class GrainDirectoryCacheBenchmark
{
    private ConcurrentLruCache<GrainId, (GrainAddress Address, int Version)> _tupleCache = null!;
    private LruGrainDirectoryCache _entryCache = null!;
    private IGrainDirectoryCacheEntrySource _entrySource = null!;
    private WeakReference<GrainDirectoryCacheEntry> _entryHandle = null!;
    private GrainId _target;

    [GlobalSetup]
    public void Setup()
    {
        _tupleCache = new(
            capacity: 1_024,
            timeToLive: TimeSpan.FromMinutes(10),
            timeProvider: TimeProvider.System);
        _entryCache = new(
            maxCacheSize: 1_024,
            maxCacheTTL: TimeSpan.FromMinutes(10),
            timeProvider: TimeProvider.System);
        _entrySource = _entryCache;

        for (var i = 0; i < 1_024; i++)
        {
            var grainId = GrainId.Create("benchmark", i.ToString());
            var address = new GrainAddress
            {
                GrainId = grainId,
                ActivationId = ActivationId.NewId(),
                SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
            };
            _tupleCache.AddOrUpdate(grainId, (address, i));
            _entryCache.AddOrUpdate(address, i);
            if (i == 512)
            {
                _target = grainId;
            }
        }

        if (!_entrySource.TryGetEntry(_target, out var entry))
        {
            throw new InvalidOperationException("Benchmark entry was not added.");
        }

        _entryHandle = entry.ReferenceHandle;
    }

    [Benchmark(Baseline = true)]
    public GrainAddress TupleValue()
    {
        _tupleCache.TryGet(_target, out var entry);
        return entry.Address;
    }

    [Benchmark]
    public GrainAddress SharedEntryRaw()
    {
        _entryCache.TryGet(_target, out var entry);
        return entry.ActivationAddress;
    }

    [Benchmark]
    public GrainAddress SharedEntryValidated()
    {
        _entryCache.LookUp(_target, out var address, out _);
        return address!;
    }

    [Benchmark]
    public GrainAddress SharedEntrySource()
    {
        _entrySource.TryGetEntry(_target, out var entry);
        return entry!.Address;
    }

    [Benchmark]
    public GrainAddress SharedHandle()
    {
        _entryHandle.TryGetTarget(out var entry);
        entry!.TryTouch();
        return entry.Address;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _tupleCache.DisposeAsync();
        await _entryCache.DisposeAsync();
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("Caching")]
public class GrainDirectoryCacheAllocationBenchmark
{
    private GrainId _grainId;
    private GrainAddress _address = null!;

    [GlobalSetup]
    public void Setup()
    {
        _grainId = GrainId.Create("benchmark", "allocation");
        _address = new GrainAddress
        {
            GrainId = _grainId,
            ActivationId = ActivationId.NewId(),
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
        };
    }

    [Benchmark(Baseline = true)]
    public object TupleEntry()
        => new ConcurrentLruCache<GrainId, (GrainAddress Address, int Version)>.LruItem(
            _grainId,
            (_address, 1));

    [Benchmark]
    public object SharedEntry() => new GrainDirectoryCacheEntry(_address, version: 1);
}
