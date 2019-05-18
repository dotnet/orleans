using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Orleans;

namespace Grains
{
    public class FasterDedicatedGrain : Grain, IFasterDedicatedGrain
    {
        private readonly ILogger<FasterDedicatedGrain> logger;
        private readonly FasterOptions options;

        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        private readonly Thread[] threads = new Thread[Environment.ProcessorCount];
        private readonly TaskCompletionSource<bool>[] completions = new TaskCompletionSource<bool>[Environment.ProcessorCount];
        private readonly BlockingCollection<Command> commands = new BlockingCollection<Command>();

        public FasterDedicatedGrain(ILogger<FasterDedicatedGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
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

            // dedicated threads will signal their completion task when done
            for (var i = 0; i < completions.Length; ++i)
            {
                completions[i] = new TaskCompletionSource<bool>();
            }

            // start a dedicated thread per logical code
            for (var i = 0; i < threads.Length; ++i)
            {
                threads[i] = new Thread(input => RunWorker((uint)input));
                threads[i].Start(i);
            }

            return Task.CompletedTask;
        }

        private void RunWorker(uint id)
        {
            // pin this thread to its own core
            Native32.AffinitizeThreadRoundRobin(id);

            try
            {
                // open a long lived session on this thread
                lookup.StartSession();

                // pull commands as they come
                foreach (var command in commands.GetConsumingEnumerable())
                {
                    switch (command)
                    {
                        case SetRangeCommand range:
                            {
                                foreach (var item in range.Items)
                                {
                                    var _key = item.Key;
                                    var _item = item;
                                    switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                                    {
                                        case Status.ERROR:
                                            range.Fault(new ApplicationException());
                                            break;

                                        default:
                                            break;
                                    }
                                }
                                lookup.Refresh();
                                range.Complete();
                            }
                            break;

                        case SetCommand set:
                            {
                                var _item = set.Item;
                                var _key = _item.Key;
                                switch (lookup.Upsert(ref _key, ref _item, Empty.Default, 0))
                                {
                                    case Status.ERROR:
                                        set.Fault(new ApplicationException());
                                        break;

                                    default:
                                        break;
                                }
                                lookup.Refresh();
                                set.Complete();
                            }
                            break;

                        case GetCommand get:
                            {
                                var _key = get.Key;
                                LookupItem input = null;
                                LookupItem output = null;
                                switch (lookup.Read(ref _key, ref input, ref output, Empty.Default, 0))
                                {
                                    case Status.ERROR:
                                        get.Fault(new ApplicationException());
                                        break;

                                    case Status.NOTFOUND:
                                        get.Complete(null);
                                        break;

                                    case Status.OK:
                                        get.Complete(output);
                                        break;

                                    case Status.PENDING:
                                        lookup.CompletePending(true);
                                        get.Complete(output);
                                        break;

                                    default:
                                        break;
                                }
                            }
                            break;

                        case SnapshotCommand snapshot:
                            {
                                lookup.CompletePending(true);
                                lookup.TakeFullCheckpoint(out var token);
                                lookup.CompleteCheckpoint(true);
                                lookup.Refresh();
                                snapshot.Complete();
                            }
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch (Exception error)
            {
                logger.LogError(error, "Dedicated thread {@ThreadId} faulted", id);
            }
            finally
            {
                try
                {
                    lookup.StopSession();
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Dedicated thread {@ThreadId} failed to stop the faster session", id);
                }

                completions[id].TrySetResult(true);
            }
        }

        public Task SetAsync(LookupItem item)
        {
            var command = new SetCommand(item);
            commands.Add(command);
            return command.Completed;
        }

        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            var command = new SetRangeCommand(items);
            commands.Add(command);
            return command.Completed;
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

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        #region Commands

        private abstract class Command
        {
        }

        private sealed class SetRangeCommand : Command
        {
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

            public SetRangeCommand(ImmutableList<LookupItem> items) => Items = items;

            public ImmutableList<LookupItem> Items { get; }
            public Task Completed => completion.Task;
            public void Complete() => completion.TrySetResult(true);
            public void Fault(Exception exception) => completion.TrySetException(exception);
        }

        private sealed class SetCommand : Command
        {
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

            public SetCommand(LookupItem item) => Item = item;

            public LookupItem Item { get; }
            public Task Completed => completion.Task;
            public void Complete() => completion.TrySetResult(true);
            public void Fault(Exception exception) => completion.TrySetException(exception);
        }

        private sealed class GetCommand : Command
        {
            private readonly TaskCompletionSource<LookupItem> completion = new TaskCompletionSource<LookupItem>();

            public GetCommand(int key) => Key = key;

            public int Key { get; }
            public Task<LookupItem> Completed => completion.Task;
            public void Complete(LookupItem item) => completion.TrySetResult(item);
            public void Fault(Exception exception) => completion.TrySetException(exception);
        }

        private sealed class SnapshotCommand : Command
        {
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

            public Task Completed => completion.Task;
            public void Complete() => completion.TrySetResult(true);
            public void Fault(Exception exception) => completion.TrySetException(exception);
        }

        #endregion
    }
}