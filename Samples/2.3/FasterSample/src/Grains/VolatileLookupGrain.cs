using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public class VolatileLookupGrain : Grain, IVolatileLookupGrain
    {
        private readonly ILogger<VolatileLookupGrain> logger;
        private readonly FasterOptions options;
        private readonly SemaphoreSlim sessionSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        private int sessionCount = 0;

        public VolatileLookupGrain(ILogger<VolatileLookupGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override async Task OnActivateAsync()
        {
            await Task.Run(() =>
            {
                // define paths
                var logPath = Path.Combine(options.HybridLogDeviceBaseDirectory, GetType().Name, this.GetPrimaryKeyString(), options.HybridLogDeviceFileTitle);
                var objectPath = Path.Combine(options.ObjectLogDeviceBaseDirectory, GetType().Name, this.GetPrimaryKeyString(), options.ObjectLogDeviceFileTitle);
                var checkpointPath = Path.Combine(options.CheckpointBaseDirectory, GetType().Name, this.GetPrimaryKeyString(), options.CheckpointContainerDirectory);

                // define the underlying log file
                logDevice = Devices.CreateLogDevice(logPath);
                objectLogDevice = Devices.CreateLogDevice(objectPath);

                // create the faster lookup
                lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                    1L << 20,
                    new LookupItemFunctions(),
                    new LogSettings()
                    {
                        LogDevice = logDevice,
                        ObjectLogDevice = objectLogDevice
                    },
                    new CheckpointSettings
                    {
                        CheckpointDir = checkpointPath
                    },
                    serializerSettings: new SerializerSettings<int, LookupItem>
                    {
                        valueSerializer = () => new ProtobufObjectSerializer<LookupItem>()
                    },
                    comparer: LookupItemFasterKeyComparer.Default);

                // attempt to recover
                if (Directory.Exists(checkpointPath) && Directory.EnumerateFiles(checkpointPath).Any())
                {
                    lookup.Recover();
                    if (!lookup.CompletePending(true))
                    {
                        throw new InvalidOperationException("Could not recover the lookup");
                    }
                }
            });

            await base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            lookup.Dispose();
            logDevice.Close();
            objectLogDevice.Close();

            return base.OnDeactivateAsync();
        }

        public async Task SetAsync(LookupItem item)
        {
            await Task.Run(async () =>
            {
                try
                {
                    await sessionSemaphore.WaitAsync();
                    lookup.StartSession();

                    Interlocked.Increment(ref sessionCount);

                    var key = item.Key;
                    lookup.Upsert(ref key, ref item, Empty.Default, 0);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Session Count = {@SessionCount}", sessionCount);
                }
                finally
                {
                    sessionSemaphore.Release();
                    try
                    {
                        Interlocked.Decrement(ref sessionCount);

                        lookup.StopSession();
                    }
                    catch (Exception stopError)
                    {
                        logger.LogError(stopError.Message);
                    }
                }
            });
        }

        public Task SetAsync(ImmutableList<LookupItem> items)
        {
            return Task.CompletedTask;
        }

        public Task<LookupItem> GetAsync(int key)
        {
            return Task.FromResult<LookupItem>(null);
        }

        public Task StartAsync() => Task.CompletedTask;

        private class WorkItem
        {
            public WorkItem(ImmutableList<LookupItem> lookupItem)
            {
                LookupItem = lookupItem;
                Completion = new TaskCompletionSource<bool>();
            }

            public ImmutableList<LookupItem> LookupItem { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }
    }
}