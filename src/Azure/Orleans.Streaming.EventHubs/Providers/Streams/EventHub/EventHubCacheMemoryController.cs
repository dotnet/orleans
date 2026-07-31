using System;
using System.Collections.Concurrent;
using System.Threading;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.EventHubs;

internal sealed class EventHubCacheMemoryController
{
    private readonly long maxActiveCacheMemory;
    private long activeBufferMemory;
    private long activeMetadataMemory;

    public EventHubCacheMemoryController(long maxActiveCacheMemory)
    {
        this.maxActiveCacheMemory = maxActiveCacheMemory;
    }

    public long ActiveCacheMemory => Volatile.Read(ref activeBufferMemory) + Volatile.Read(ref activeMetadataMemory);

    public bool IsUnderPressure => ActiveCacheMemory >= maxActiveCacheMemory;

    public void AddActiveBufferMemory(int size) => Interlocked.Add(ref activeBufferMemory, size);

    public void RemoveActiveBufferMemory(int size) => Interlocked.Add(ref activeBufferMemory, -size);

    public void AdjustActiveMetadataMemory(long delta) => Interlocked.Add(ref activeMetadataMemory, delta);
}

internal interface IEventHubCacheBufferPool : IObjectPool<FixedSizeBuffer>
{
    FixedSizeBuffer Allocate(int minimumSize);

    long ActiveMemory { get; }

    long PooledMemory { get; }
}

internal sealed class EventHubCacheBufferPool : IEventHubCacheBufferPool
{
    internal const int MinBufferSize = 64 * 1024;
    internal const int MaxBufferSize = 1024 * 1024;

    private static readonly int[] BufferSizes = [MinBufferSize, 128 * 1024, 256 * 1024, 512 * 1024, MaxBufferSize];

    private readonly ConcurrentStack<FixedSizeBuffer>[] pools;
    private readonly EventHubCacheMemoryController memoryController;
    private readonly long maxPooledMemory;
    private readonly IBlockPoolMonitor? monitor;
    private readonly long monitorIntervalMilliseconds;
    private long pooledMemory;
    private long activeMemory;
    private long nextMonitorTimestamp;

    public EventHubCacheBufferPool(
        EventHubCacheMemoryController memoryController,
        long maxPooledMemory,
        IBlockPoolMonitor? monitor,
        TimeSpan monitorInterval)
    {
        this.memoryController = memoryController;
        this.maxPooledMemory = maxPooledMemory;
        this.monitor = monitor;
        monitorIntervalMilliseconds = Math.Max(1, (long)monitorInterval.TotalMilliseconds);
        nextMonitorTimestamp = Environment.TickCount64 + monitorIntervalMilliseconds;
        pools = new ConcurrentStack<FixedSizeBuffer>[BufferSizes.Length];
        for (var i = 0; i < pools.Length; i++)
        {
            pools[i] = new ConcurrentStack<FixedSizeBuffer>();
        }
    }

    public long ActiveMemory => Volatile.Read(ref activeMemory);

    public long PooledMemory => Volatile.Read(ref pooledMemory);

    public FixedSizeBuffer Allocate() => Allocate(MaxBufferSize);

    public FixedSizeBuffer Allocate(int minimumSize)
    {
        var poolIndex = GetPoolIndex(minimumSize);
        var size = BufferSizes[poolIndex];
        if (pools[poolIndex].TryPop(out var result))
        {
            Interlocked.Add(ref pooledMemory, -size);
        }
        else
        {
            result = new FixedSizeBuffer(size);
        }

        result.Pool = this;
        Interlocked.Add(ref activeMemory, size);
        memoryController.AddActiveBufferMemory(size);
        monitor?.TrackMemoryAllocated(size);
        TryReport();
        return result;
    }

    public void Free(FixedSizeBuffer resource)
    {
        var size = resource.SizeInByte;
        Interlocked.Add(ref activeMemory, -size);
        memoryController.RemoveActiveBufferMemory(size);
        monitor?.TrackMemoryReleased(size);

        if (TryReservePooledMemory(size))
        {
            pools[GetPoolIndex(size)].Push(resource);
        }

        TryReport();
    }

    private static int GetPoolIndex(int minimumSize)
    {
        for (var i = 0; i < BufferSizes.Length; i++)
        {
            if (minimumSize <= BufferSizes[i])
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(minimumSize), $"The requested buffer size of {minimumSize} bytes exceeds the maximum of {MaxBufferSize} bytes.");
    }

    private bool TryReservePooledMemory(int size)
    {
        while (true)
        {
            var current = Volatile.Read(ref pooledMemory);
            if (current > maxPooledMemory - size)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref pooledMemory, current + size, current) == current)
            {
                return true;
            }
        }
    }

    private void TryReport()
    {
        if (monitor is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var next = Volatile.Read(ref nextMonitorTimestamp);
        if (now < next || Interlocked.CompareExchange(ref nextMonitorTimestamp, now + monitorIntervalMilliseconds, next) != next)
        {
            return;
        }

        var active = ActiveMemory;
        var pooled = PooledMemory;
        monitor.Report(active + pooled, pooled, active);
    }
}
