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
        private readonly TaskCompletionSource<bool>[] completions = new TaskCompletionSource<bool>[Environment.ProcessorCount];
        private readonly BlockingCollection<Command> commands = new BlockingCollection<Command>();

        private readonly MyCommandPool commandPool = new MyCommandPool();

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
            if (lookup != null) throw new ArgumentException();

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
                completions[i] = new TaskCompletionSource<bool>();
            }

            // start a dedicated thread per logical code
            for (var i = 0; i < threads.Length; ++i)
            {
                threads[i] = new Thread(() => RunWorker((uint)id));
                threads[i].Start();
            }

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

                // let the grain know the thread completed gracefully
                completions[id].TrySetResult(true);
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
            var command = commandPool.GetSet();
            try
            {
                command.Item = item;
                commands.Add(command);
                return command.Completed;
            }
            finally
            {
                commandPool.Return(command);
            }
        }

        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            var command = commandPool.GetSetRange();
            try
            {
                command.Items = items;
                commands.Add(command);
                return command.Completed;
            }
            finally
            {
                commandPool.Return(command);
            }
        }

        public Task SnapshotAsync()
        {
            var command = commandPool.GetSnapshot();
            try
            {
                commands.Add(command);
                return command.Completed;
            }
            finally
            {
                commandPool.Return(command);
            }
        }

        public Task<LookupItem> TryGetAsync(int key)
        {
            var command = commandPool.GetGet();
            try
            {
                command.Key = key;
                commands.Add(command);
                return command.Completed;
            }
            finally
            {
                commandPool.Return(command);
            }
        }

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        #endregion Command Methods

        #region Commands

        private abstract class Command
        {
            public abstract void Execute(MyFasterKV lookup);

            public abstract void Reset();
        }

        private sealed class SetRangeCommand : Command
        {
            public ImmutableList<LookupItem> Items { get; set; }

            private TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                var okay = true;
                try
                {
                    foreach (var item in Items)
                    {
                        var _key = item.Key;
                        var _item = item;
                        switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                        {
                            case Status.ERROR:
                                okay = false;
                                break;

                            default:
                                break;
                        }
                    }
                    lookup.Refresh();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    return;
                }

                if (okay)
                {
                    completion.TrySetResult(true);
                }
                else
                {
                    completion.TrySetException(new ApplicationException());
                }
            }

            public override void Reset()
            {
                Items = null;
                completion = new TaskCompletionSource<bool>();
            }
        }

        private sealed class SetCommand : Command
        {
            public LookupItem Item { get; set; }

            private TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                var okay = true;
                try
                {
                    var _item = Item;
                    var _key = _item.Key;
                    switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                    {
                        case Status.ERROR:
                            okay = false;
                            break;

                        default:
                            break;
                    }
                    lookup.Refresh();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    return;
                }

                if (okay)
                {
                    completion.TrySetResult(true);
                }
                else
                {
                    completion.TrySetException(new ApplicationException());
                }
            }

            public override void Reset()
            {
                Item = null;
                completion = new TaskCompletionSource<bool>();
            }
        }

        private sealed class GetCommand : Command
        {
            public int Key { get; set; }

            private TaskCompletionSource<LookupItem> completion = new TaskCompletionSource<LookupItem>();
            public Task<LookupItem> Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                var okay = true;
                LookupItem output = null;
                try
                {
                    var _key = Key;
                    LookupItem input = null;
                    switch (lookup.Read(ref _key, ref input, ref output, Empty.Default, 0))
                    {
                        case Status.ERROR:
                            okay = false;
                            break;

                        case Status.PENDING:
                            lookup.CompletePending(true);
                            break;

                        default:
                            break;
                    }
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    return;
                }

                if (okay)
                {
                    completion.TrySetResult(output);
                }
                else
                {
                    completion.TrySetException(new ApplicationException());
                }
            }

            public override void Reset()
            {
                Key = default;
                completion = new TaskCompletionSource<LookupItem>();
            }
        }

        private sealed class SnapshotCommand : Command
        {
            private TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            public Task Completed => completion.Task;

            public override void Execute(MyFasterKV lookup)
            {
                var okay = true;
                try
                {
                    okay &= lookup.CompletePending(true);
                    okay &= lookup.TakeFullCheckpoint(out var token);
                    okay &= lookup.CompleteCheckpoint(true);
                    lookup.Refresh();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    return;
                }

                if (okay)
                {
                    completion.TrySetResult(true);
                }
                else
                {
                    completion.TrySetException(new ApplicationException());
                }
            }

            public override void Reset() => completion = new TaskCompletionSource<bool>();
        }

        #endregion Commands

        #region Command Pool

        private sealed class MyCommandPool
        {
            private readonly DefaultObjectPool<SetRangeCommand> setRangeCommandPool = new DefaultObjectPool<SetRangeCommand>(new SetRangeCommandPooledObjectPolicy());
            private readonly DefaultObjectPool<SetCommand> setCommandPool = new DefaultObjectPool<SetCommand>(new SetCommandPooledObjectPolicy());
            private readonly DefaultObjectPool<GetCommand> getCommandPool = new DefaultObjectPool<GetCommand>(new GetCommandPooledObjectPolicy());
            private readonly DefaultObjectPool<SnapshotCommand> snapshotCommandPool = new DefaultObjectPool<SnapshotCommand>(new SnapshotCommandPooledObjectPolicy());

            public SetRangeCommand GetSetRange() => setRangeCommandPool.Get();

            public SetCommand GetSet() => setCommandPool.Get();

            public GetCommand GetGet() => getCommandPool.Get();

            public SnapshotCommand GetSnapshot() => snapshotCommandPool.Get();

            public void Return(SetRangeCommand command) => setRangeCommandPool.Return(command);

            public void Return(SetCommand command) => setCommandPool.Return(command);

            public void Return(GetCommand command) => getCommandPool.Return(command);

            public void Return(SnapshotCommand command) => snapshotCommandPool.Return(command);

            private sealed class SetRangeCommandPooledObjectPolicy : PooledObjectPolicy<SetRangeCommand>
            {
                public override SetRangeCommand Create() => new SetRangeCommand();

                public override bool Return(SetRangeCommand obj)
                {
                    obj.Reset(); return true;
                }
            }

            private sealed class SetCommandPooledObjectPolicy : PooledObjectPolicy<SetCommand>
            {
                public override SetCommand Create() => new SetCommand();

                public override bool Return(SetCommand obj)
                {
                    obj.Reset(); return true;
                }
            }

            private sealed class GetCommandPooledObjectPolicy : PooledObjectPolicy<GetCommand>
            {
                public override GetCommand Create() => new GetCommand();

                public override bool Return(GetCommand obj)
                {
                    obj.Reset(); return true;
                }
            }

            private sealed class SnapshotCommandPooledObjectPolicy : PooledObjectPolicy<SnapshotCommand>
            {
                public override SnapshotCommand Create() => new SnapshotCommand();

                public override bool Return(SnapshotCommand obj)
                {
                    obj.Reset(); return true;
                }
            }
        }

        #endregion Command Pool
    }
}