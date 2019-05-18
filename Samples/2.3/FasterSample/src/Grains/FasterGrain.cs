using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class FasterGrain : Grain, IFasterGrain
    {
        private readonly ILogger<FasterGrain> logger;
        private readonly FasterOptions options;

        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        /// <summary>
        /// There is a hard limit on the number of concurrent sessions on the faster lookup.
        /// This semaphore prevents creating more concurrent sessions than the number of logical processors when using the thread pool.
        /// </summary>
        private SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        public FasterGrain(ILogger<FasterGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        /// <summary>
        /// Releases the lookup resources on graceful grain deactivation.
        /// </summary>
        /// <returns></returns>
        public override async Task OnDeactivateAsync()
        {
            try
            {
                lookup.Dispose();
            }
            finally
            {
                try
                {
                    logDevice.Close();
                }
                finally
                {
                    objectLogDevice.Close();
                }
            }

            lookup = null;
            logDevice = null;
            objectLogDevice = null;

            await base.OnDeactivateAsync();
        }

        /// <summary>
        /// This sets up a faster lookup as per the given parameters.
        /// This code is only here to facilitate benchmarking.
        /// In a production design, the code below would sit in OnActivateAsync() with parameters taken from injected options.
        /// </summary>
        /// <param name="hashBuckets">The number of hash buckets in the key space.</param>
        /// <param name="memorySizeBits">The power of two size for the in-memory log portion size.</param>
        /// <param name="checkpointType">Whether to take a full snapshot of state or just fold over the log.</param>
        /// <returns></returns>
        public Task StartAsync(int hashBuckets, int memorySizeBits)
        {
            // must call release before configuring again
            if (lookup != null) throw new InvalidOperationException();

            // define the base folder to hold the dictionary state of this grain
            var grainPath = Path.Combine(options.BaseDirectory, GetType().Name, this.GetPrimaryKey().ToString("D"));

            // ensure said folder exists
            if (!Directory.Exists(grainPath))
            {
                Directory.CreateDirectory(grainPath);
            }

            // define the paths for the log files
            var logPath = Path.Combine(grainPath, options.HybridLogDeviceFileTitle);
            var objectPath = Path.Combine(grainPath, options.ObjectLogDeviceFileTitle);

            // define the sub-folder to hold checkpoints
            var checkpointPath = Path.Combine(grainPath, options.CheckpointsSubDirectory);

            // ensure said folder exists
            if (!Directory.Exists(checkpointPath))
            {
                Directory.CreateDirectory(checkpointPath);
            }

            // define the underlying log devices using the default file providers
            logDevice = Devices.CreateLogDevice(logPath, true, true);
            objectLogDevice = Devices.CreateLogDevice(objectPath, true, true);

            // create the faster lookup now
            lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                hashBuckets, // hash buckets * 64 bytes per bucket = memory spent by hash table key space
                new LookupItemFunctions(),
                new LogSettings()
                {
                    LogDevice = logDevice,
                    ObjectLogDevice = objectLogDevice,

                    // 2^(memorySizeBits) = memory spent by in-memory log portion
                    // e.g. 2^30 = 1GB in-memory log (the overflow is spilt to disk)
                    MemorySizeBits = memorySizeBits
                },
                new CheckpointSettings
                {
                    CheckpointDir = checkpointPath,
                    CheckPointType = CheckpointType.FoldOver
                },
                serializerSettings: new SerializerSettings<int, LookupItem>
                {
                    valueSerializer = () => new LookupItemSerializer()
                },
                comparer: LookupItemFasterKeyComparer.Default);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Issues graceful deactivation.
        /// </summary>
        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets a single item in the lookup.
        /// This is a blind update.
        /// </summary>
        /// <param name="item">The item to set.</param>
        /// <returns></returns>
        public Task SetAsync(LookupItem item)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();

                    var key = item.Key;
                    lookup.Upsert(ref key, ref item, Empty.Default, 0);
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
            });
        }

        /// <summary>
        /// Sets a range of item in the lookup.
        /// This is a blind update.
        /// </summary>
        /// <param name="items">The items to set.</param>
        /// <returns></returns>
        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    for (var i = 0; i < items.Count; ++i)
                    {
                        var item = items[i];
                        var key = item.Key;
                        lookup.Upsert(ref key, ref item, Empty.Default, i);
                    }
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                return Task.CompletedTask;
            });
        }

        public Task SnapshotAsync()
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    lookup.CompletePending(true);
                    lookup.TakeFullCheckpoint(out var token);
                    lookup.CompleteCheckpoint(true);
                    lookup.StopSession();
                }
                finally
                {
                    semaphore.Release();
                }
                return Task.CompletedTask;
            });
        }

        public Task<LookupItem> TryGetAsync(int key)
        {
            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();
                LookupItem result = null;
                if (lookup.Read(ref key, ref result, ref result, Empty.Default, 0) == Status.ERROR)
                {
                    throw new ApplicationException();
                }
                return Task.FromResult(result);
            }
            finally
            {
                if (session != Guid.Empty)
                {
                    lookup.StopSession();
                }
            }
        }

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> items)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    for (var i = 0; i < items.Count; ++i)
                    {
                        var item = items[i];
                        var key = item.Key;
                        lookup.RMW(ref key, ref item, Empty.Default, i);
                    }
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                return Task.CompletedTask;
            });
        }
    }
}