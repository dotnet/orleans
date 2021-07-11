using System;
using System.Buffers;
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
using MyFasterKV = FASTER.core.FasterKV<int, Grains.Models.LookupItem, Grains.Models.LookupItem, Grains.Models.LookupItem, System.Threading.Tasks.TaskCompletionSource<Grains.Models.LookupItem>, Grains.LookupItemFunctions>;

namespace Grains
{
    [Reentrant]
    public class FasterThreadPoolGrain : Grain, IFasterThreadPoolGrain
    {
        private readonly ILogger<FasterThreadPoolGrain> logger;
        private readonly FasterOptions options;

        private string grainType;
        private Guid grainKey;

        private IDevice logDevice;
        private IDevice objectLogDevice;
        private MyFasterKV lookup;

        /// <summary>
        /// There is a hard limit on the number of concurrent sessions on the faster lookup.
        /// This semaphore prevents creating more concurrent sessions than the number of logical processors when using the thread pool.
        /// </summary>
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        public FasterThreadPoolGrain(ILogger<FasterThreadPoolGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override Task OnActivateAsync()
        {
            grainType = GetType().Name;
            grainKey = this.GetPrimaryKey();

            return base.OnActivateAsync();
        }

        /// <summary>
        /// Releases the lookup resources on graceful grain deactivation.
        /// </summary>
        /// <returns></returns>
        public override Task OnDeactivateAsync()
        {
            try
            {
                lookup?.Dispose();
            }
            catch (Exception error)
            {
                logger.LogError(error,
                    "{@GrainType} {@GrainKey} failed to dispose of the faster lookup",
                    grainType, grainKey);
            }
            finally
            {
                try
                {
                    logDevice?.Close();
                }
                catch (Exception error)
                {
                    logger.LogError(error,
                        "{@GrainType} {@GrainKey} failed to close the log device",
                        grainType, grainKey);
                }
                finally
                {
                    try
                    {
                        objectLogDevice?.Close();
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error,
                            "{@GrainType} {@GrainKey} failed to close the object log device",
                            grainType, grainKey);
                    }
                }
            }

            return base.OnDeactivateAsync();
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
            lookup = new MyFasterKV(
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
        public Task SetAsync(LookupItem item) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();

                var key = item.Key;
                var context = new TaskCompletionSource<LookupItem>();

                lookup.Upsert(ref key, ref item, context, 0);
                lookup.CompletePending();
                await context.Task;
            }
            finally
            {
                try
                {
                    if (session != Guid.Empty) lookup.StopSession();
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        /// <summary>
        /// Sets a range of item in the lookup.
        /// This is a blind update.
        /// </summary>
        /// <param name="items">The items to set.</param>
        /// <returns></returns>
        public Task SetRangeAsync(ImmutableList<LookupItem> items) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            var completions = ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Rent(items.Count);

            try
            {
                session = lookup.StartSession();

                for (var i = 0; i < items.Count; ++i)
                {
                    var item = items[i];
                    var key = item.Key;
                    completions[i] = new TaskCompletionSource<LookupItem>();

                    switch (lookup.Upsert(ref key, ref item, completions[i], i))
                    {
                        case Status.OK:
                            completions[i].SetResult(item);
                            break;

                        case Status.ERROR:
                            completions[i].SetException(new ApplicationException());
                            break;

                        default:
                            break;
                    }
                }

                lookup.CompletePending();
                for (var i = 0; i < items.Count; ++i)
                {
                    await completions[i].Task;
                }
            }
            finally
            {
                try
                {
                    ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Return(completions);
                }
                finally
                {
                    try
                    {
                        if (session != Guid.Empty) lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
            }
        });

        public Task SnapshotAsync() => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();
                if (!lookup.CompletePending(true)) throw new ApplicationException();
                if (!lookup.TakeFullCheckpoint(out var token)) throw new ApplicationException();
                if (!lookup.CompleteCheckpoint(true)) throw new ApplicationException();
            }
            finally
            {
                try
                {
                    if (session != Guid.Empty) lookup.StopSession();
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        public Task<LookupItem> TryGetAsync(int key) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();

                var context = new TaskCompletionSource<LookupItem>();
                LookupItem result = null;
                lookup.Read(ref key, ref result, ref result, context, 0);
                lookup.CompletePending();
                return await context.Task;
            }
            finally
            {
                try
                {
                    if (session != Guid.Empty) lookup.StopSession();
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        public Task<ImmutableList<LookupItem>> TryGetRangeAsync(ImmutableList<int> keys) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            var completions = ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Rent(keys.Count);

            try
            {
                session = lookup.StartSession();
                var result = ImmutableList.CreateBuilder<LookupItem>();

                for (var i = 0; i < keys.Count; ++i)
                {
                    var key = keys[i];
                    LookupItem input = null;
                    LookupItem output = null;
                    completions[i] = new TaskCompletionSource<LookupItem>();

                    lookup.Read(ref key, ref input, ref output, completions[i], i);
                }
                lookup.CompletePending();

                for (var i = 0; i < keys.Count; ++i)
                {
                    var item = await completions[i].Task;
                    if (item != null)
                    {
                        result.Add(item);
                    }
                }

                return result.ToImmutable();
            }
            finally
            {
                try { ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Return(completions); }
                catch (Exception error) { logger.LogError(error, error.Message); }

                try { if (session != Guid.Empty) lookup.StopSession(); }
                catch (Exception error) { logger.LogError(error, error.Message); }

                try { semaphore.Release(); }
                catch (Exception error) { logger.LogError(error, error.Message); }
            }
        });

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> deltas) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();

            var session = Guid.Empty;
            var completions = ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Rent(deltas.Count);

            try
            {
                session = lookup.StartSession();

                for (var i = 0; i < deltas.Count; ++i)
                {
                    var item = deltas[i];
                    var key = item.Key;
                    completions[i] = new TaskCompletionSource<LookupItem>();

                    lookup.RMW(ref key, ref item, completions[i], i);
                }
                lookup.CompletePending();

                for (var i = 0; i < deltas.Count; ++i)
                {
                    await completions[i].Task;
                }
            }
            finally
            {
                try { ArrayPool<TaskCompletionSource<LookupItem>>.Shared.Return(completions); }
                catch (Exception error) { logger.LogError(error, error.Message); }

                try { if (session != Guid.Empty) lookup.StopSession(); }
                catch (Exception error) { logger.LogError(error, error.Message); }

                try { semaphore.Release(); }
                catch (Exception error) { logger.LogError(error, error.Message); }
            }
        });
    }
}