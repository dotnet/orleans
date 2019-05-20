using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using MyFasterKV = FASTER.core.FasterKV<int, Grains.Models.LookupItem, Grains.Models.LookupItem, Grains.Models.LookupItem, FASTER.core.Empty, Grains.LookupItemFunctions>;

namespace Grains
{
    [Reentrant]
    public class FasterDedicatedGrain : Grain, IFasterDedicatedGrain
    {
        private readonly ILogger<FasterDedicatedGrain> logger;
        private readonly FasterOptions options;

        private IDevice logDevice;
        private IDevice objectLogDevice;
        private MyFasterKV lookup;

        private readonly Thread[] threads = new Thread[Environment.ProcessorCount];
        private readonly TaskCompletionSource<object>[] completions = new TaskCompletionSource<object>[Environment.ProcessorCount];
        private readonly BlockingCollection<Command> commands = new BlockingCollection<Command>();

        private string grainType;
        private Guid grainKey;

        public FasterDedicatedGrain(ILogger<FasterDedicatedGrain> logger, IOptions<FasterOptions> options)
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
        /// This sets up a faster lookup as per the given parameters.
        /// This code is only here to facilitate benchmarking.
        /// In a production design, the code below would sit in OnActivateAsync() with parameters taken from injected options.
        /// </summary>
        /// <param name="hashBuckets">The number of hash buckets in the key space.</param>
        /// <param name="memorySizeBits">The power of two size for the in-memory log portion size.</param>
        /// <param name="checkpointType">Whether to take a full snapshot of state or just fold over the log.</param>
        /// <param name="dedicated">Whether to use dedicated threads per logical processor or default to the thread pool.</param>
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

            // dedicated threads will signal their completion task when done
            for (var i = 0; i < completions.Length; ++i)
            {
                completions[i] = new TaskCompletionSource<object>();
            }

            // start a dedicated thread per logical code
            for (var i = 0; i < threads.Length; ++i)
            {
                threads[i] = new Thread(id => RunWorker((uint)id));
                threads[i].Start((uint)i);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private void RunWorker(uint id)
        {
            // pin this thread to its own core
            Native32.AffinitizeThreadRoundRobin(id);

            var session = Guid.Empty;
            try
            {
                // start a long lived session on this thread
                session = lookup.StartSession();

                // pull commands as they come
                foreach (var command in commands.GetConsumingEnumerable())
                {
                    command.Execute(lookup);
                }
            }
            catch (Exception error)
            {
                logger.LogError(error, "Dedicated thread {@ThreadId} faulted", id);

                // let the grain know why the thread faulted
                completions[id].TrySetException(error);
            }
            finally
            {
                // attempt to stop the session if we indeed acquired it
                if (session != Guid.Empty)
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Dedicated thread {@ThreadId} failed to stop the faster session", id);
                    }
                }

                // let the grain know the thread completed gracefully
                // unless the catch block has already set an exception
                completions[id].TrySetResult(true);
            }
        }

        public override async Task OnDeactivateAsync()
        {
            logger.LogInformation(
                "{@GrainType} {@GrainKey} is deactivating",
                grainType, grainKey);

            // signal the threads that work is complete
            commands.CompleteAdding();

            // wait for thread completion while unwrapping any exceptions
            for (var i = 0; i < completions.Length; ++i)
            {
                try
                {
                    await completions[i].Task;

                    logger.LogInformation(
                        "{@GrainType} {@GrainKey} faster work thread {@ThreadId} completed gracefully",
                        grainType, grainKey, i);
                }
                catch (Exception error)
                {
                    logger.LogError(error,
                        "{@GrainType} {@GrainKey} faster worker thread {@ThreadId} faulted",
                        grainType, grainKey, i);
                }
            }

            // attempt to release faster resources
            try
            {
                lookup.Dispose();
            }
            catch (Exception error)
            {
                logger.LogError(error,
                    "{@GrainType} {@GrainKey} failed to dispose of faster lookup",
                    grainType, grainKey);
            }

            // attempt to close the faster log device
            try
            {
                logDevice.Close();
            }
            catch (Exception error)
            {
                logger.LogError(error,
                    "{@GrainType} {@GrainKey} failed to close the faster log device",
                    grainType, grainKey);
            }

            // attempt to close the faster object log device
            try
            {
                objectLogDevice.Close();
            }
            catch (Exception error)
            {
                logger.LogError(error,
                    "{@GrainType} {@GrainKey} failed to close the faster object log device",
                    grainType, grainKey);
            }

            await base.OnDeactivateAsync();
        }

        #region Command Methods

        public Task SetAsync(LookupItem item)
        {
            var command = new SetCommand(item);
            commands.Add(command);
            return command.Completed;
        }

        public async Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            var command = setRangeCommandPool.Get();
            try
            {
                command.Items = items;
                commands.Add(command);
                await command.Completed;
            }
            finally
            {
                setRangeCommandPool.Return(command);
            }
        }

        public Task SnapshotAsync()
        {
            var command = new SnapshotCommand();
            commands.Add(command);
            return command.Completed;
        }

        public Task<LookupItem> TryGetAsync(int key)
        {
            var command = new GetCommand(key);
            commands.Add(command);
            return command.Completed;
        }

        public Task<ImmutableList<LookupItem>> TryGetRangeAsync(ImmutableList<int> keys)
        {
            var command = new GetRangeCommand(keys);
            commands.Add(command);
            return command.Completed;
        }

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> deltas)
        {
            var command = new SetRangeDeltaCommand(deltas);
            commands.Add(command);
            return command.Completed;
        }

        #endregion Command Methods

        #region Commands

        private abstract class Command
        {
            public abstract void Execute(MyFasterKV lookup);
        }

        private sealed class SetRangeCommand : Command
        {
            public ImmutableList<LookupItem> Items { get; set; }
            private TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                if (Items == null || Items.IsEmpty) return;

                try
                {
                    foreach (var item in Items)
                    {
                        var _key = item.Key;
                        var _item = item;
                        switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                        {
                            case Status.OK:
                            case Status.NOTFOUND:
                            case Status.PENDING:
                                break;

                            case Status.ERROR:
                                completion.TrySetException(new ApplicationException());
                                lookup.Refresh();
                                return;

                            default:
                                completion.TrySetException(new ArgumentOutOfRangeException());
                                lookup.Refresh();
                                return;
                        }
                    }
                    completion.TrySetResult(true);
                    lookup.Refresh();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }

            public void Reset()
            {
                Items = null;
                completion = new TaskCompletionSource<bool>();
            }
        }

        private sealed class SetRangeDeltaCommand : Command
        {
            private readonly ImmutableList<LookupItem> deltas;
            private readonly TaskCompletionSource<object> completion = new TaskCompletionSource<object>();

            public SetRangeDeltaCommand(ImmutableList<LookupItem> deltas)
            {
                this.deltas = deltas ?? throw new ArgumentNullException(nameof(deltas));
            }

            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                try
                {
                    foreach (var delta in deltas)
                    {
                        var _key = delta.Key;
                        var _delta = delta;
                        switch (lookup.RMW(ref _key, ref _delta, Empty.Default, 0))
                        {
                            case Status.OK:
                                break;

                            case Status.NOTFOUND:
                                switch (lookup.Upsert(ref _key, ref _delta, Empty.Default, 0))
                                {
                                    case Status.OK:
                                    case Status.NOTFOUND:
                                    case Status.PENDING:
                                        break;

                                    case Status.ERROR:
                                        lookup.Refresh();
                                        completion.TrySetException(new ApplicationException());
                                        return;

                                    default:
                                        lookup.Refresh();
                                        completion.TrySetException(new ArgumentOutOfRangeException());
                                        return;
                                }
                                break;

                            case Status.PENDING:
                                break;

                            case Status.ERROR:
                                lookup.Refresh();
                                completion.TrySetException(new ApplicationException());
                                return;

                            default:
                                lookup.Refresh();
                                completion.TrySetException(new ArgumentOutOfRangeException());
                                return;
                        }
                    }
                    lookup.Refresh();
                    completion.TrySetResult(null);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        private sealed class SetCommand : Command
        {
            private readonly LookupItem item;
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

            public SetCommand(LookupItem item)
            {
                this.item = item ?? throw new ArgumentNullException(nameof(item));
            }

            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                try
                {
                    var _key = item.Key;
                    var _item = item;
                    switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                    {
                        case Status.OK:
                        case Status.NOTFOUND:
                        case Status.PENDING:
                            break;

                        case Status.ERROR:
                            lookup.Refresh();
                            completion.TrySetException(new ApplicationException());
                            break;

                        default:
                            lookup.Refresh();
                            completion.TrySetException(new ArgumentOutOfRangeException());
                            break;
                    }
                    lookup.Refresh();
                    completion.TrySetResult(true);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        private sealed class GetCommand : Command
        {
            private readonly int key;
            private readonly TaskCompletionSource<LookupItem> completion = new TaskCompletionSource<LookupItem>();

            public GetCommand(int key)
            {
                this.key = key;
            }

            public Task<LookupItem> Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                try
                {
                    var _key = key;
                    LookupItem input = null;
                    LookupItem output = null;
                    switch (lookup.Read(ref _key, ref input, ref output, Empty.Default, 0))
                    {
                        case Status.OK:
                            completion.TrySetResult(output);
                            break;

                        case Status.NOTFOUND:
                            completion.TrySetResult(null);
                            break;

                        case Status.PENDING:
                            if (lookup.CompletePending(true))
                            {
                                completion.TrySetResult(output);
                            }
                            else
                            {
                                completion.TrySetException(new ApplicationException());
                            }
                            break;

                        case Status.ERROR:
                            completion.TrySetException(new ApplicationException());
                            break;

                        default:
                            completion.TrySetException(new ArgumentOutOfRangeException());
                            break;
                    }
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        private sealed class GetRangeCommand : Command
        {
            private readonly ImmutableList<int> keys;
            private readonly TaskCompletionSource<ImmutableList<LookupItem>> completion = new TaskCompletionSource<ImmutableList<LookupItem>>();

            public GetRangeCommand(ImmutableList<int> keys)
            {
                this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
            }

            public Task<ImmutableList<LookupItem>> Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                try
                {
                    var result = ImmutableList.CreateBuilder<LookupItem>();
                    foreach (var key in keys)
                    {
                        var _key = key;
                        LookupItem input = null;
                        LookupItem output = null;
                        switch (lookup.Read(ref _key, ref input, ref output, Empty.Default, 0))
                        {
                            case Status.OK:
                                result.Add(output);
                                break;

                            case Status.NOTFOUND:
                                break;

                            case Status.PENDING:
                                if (lookup.CompletePending(true))
                                {
                                    result.Add(output);
                                }
                                else
                                {
                                    completion.TrySetException(new ApplicationException());
                                    return;
                                }
                                break;

                            case Status.ERROR:
                                completion.TrySetException(new ApplicationException());
                                return;

                            default:
                                completion.TrySetException(new ArgumentOutOfRangeException());
                                return;
                        }
                    }
                    completion.TrySetResult(result.ToImmutable());
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        private sealed class SnapshotCommand : Command
        {
            private TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                try
                {
                    if (lookup.CompletePending(true) &&
                        lookup.TakeFullCheckpoint(out var token) &&
                        lookup.CompleteCheckpoint(true))
                    {
                        completion.TrySetResult(true);
                    }
                    else
                    {
                        completion.TrySetException(new ApplicationException());
                    }
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        #endregion Commands

        #region Command Pools

        private class SetRangeCommandPooledObjectPolicy : DefaultPooledObjectPolicy<SetRangeCommand>
        {
            public override SetRangeCommand Create() => new SetRangeCommand();

            public override bool Return(SetRangeCommand obj)
            {
                obj.Reset();
                return true;
            }
        }

        private readonly DefaultObjectPool<SetRangeCommand> setRangeCommandPool = new DefaultObjectPool<SetRangeCommand>(new SetRangeCommandPooledObjectPolicy());

        #endregion Command Pools
    }
}