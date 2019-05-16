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
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        public FasterGrain(ILogger<FasterGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override Task OnActivateAsync()
        {
            // define paths
            var grainPath = Path.Combine(options.BaseDirectory, GetType().Name, this.GetPrimaryKey().ToString("D"));
            if (!Directory.Exists(grainPath))
            {
                Directory.CreateDirectory(grainPath);
            }
            var logPath = Path.Combine(grainPath, options.HybridLogDeviceFileTitle);
            var objectPath = Path.Combine(grainPath, options.ObjectLogDeviceFileTitle);
            var checkpointPath = Path.Combine(grainPath, options.CheckpointsSubDirectory);
            if (!Directory.Exists(checkpointPath))
            {
                Directory.CreateDirectory(checkpointPath);
            }

            // define the underlying log file
            logDevice = Devices.CreateLogDevice(logPath, true, true);
            objectLogDevice = Devices.CreateLogDevice(objectPath, true, true);

            // create the faster lookup
            lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                1L << 20, // 2^20 hash buckets * 64 bytes per bucket = 64MB key space
                new LookupItemFunctions(),
                new LogSettings()
                {
                    LogDevice = logDevice,
                    ObjectLogDevice = objectLogDevice,
                    MemorySizeBits = 30 // 2^30 bytes = 1GB log space
                },
                new CheckpointSettings
                {
                    CheckpointDir = checkpointPath,
                    CheckPointType = CheckpointType.Snapshot
                },
                serializerSettings: new SerializerSettings<int, LookupItem>
                {
                    valueSerializer = () => new LookupItemSerializer()
                },
                comparer: LookupItemFasterKeyComparer.Default);

            // attempt recovery
            /*
            try
            {
                lookup.Recover();
                logger.LogWarning("Recovered {@ItemsRecovered} entries", lookup.EntryCount);
            }
            catch (DirectoryNotFoundException)
            {
                logger.LogWarning("Nothing to recover from");
            }
            catch (InvalidOperationException)
            {
                logger.LogWarning("Nothing to recover from");
            }
            */

            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            lookup.Dispose();
            logDevice.Close();
            objectLogDevice.Close();

            return base.OnDeactivateAsync();
        }

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task SetAsync(LookupItem item)
        {
            await semaphore.WaitAsync();

            try
            {
                lookup.StartSession();

                var key = item.Key;
                lookup.Upsert(ref key, ref item, Empty.Default, 0);
                lookup.Refresh();
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
        }

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
                        if (i % 1024 == 0)
                        {
                            lookup.Refresh();
                        }

                        var item = items[i];
                        var key = item.Key;
                        lookup.Upsert(ref key, ref item, Empty.Default, i);
                    }
                    lookup.CompletePending(true);
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