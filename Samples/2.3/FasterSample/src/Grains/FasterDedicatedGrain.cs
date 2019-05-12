using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    public class FasterDedicatedGrain : Grain, IFasterDedicatedGrain
    {
        private readonly ILogger<FasterDedicatedGrain> logger;
        private readonly FasterOptions options;
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        private readonly Thread[] threads = new Thread[Environment.ProcessorCount];

        private readonly TaskCompletionSource<bool>[] threadCompletions =
            Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => new TaskCompletionSource<bool>())
            .ToArray();

        private readonly BlockingCollection<Command>[] queues =
            Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => new BlockingCollection<Command>())
            .ToArray();

        private int targetQueue;

        public FasterDedicatedGrain(ILogger<FasterDedicatedGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override Task OnActivateAsync()
        {
            // define paths
            var logPath = Path.Combine(options.BaseDirectory, GetType().Name, this.GetPrimaryKey().ToString("D"), options.HybridLogDeviceFileTitle);
            var objectPath = Path.Combine(options.BaseDirectory, GetType().Name, this.GetPrimaryKey().ToString("D"), options.ObjectLogDeviceFileTitle);

            // define the underlying log file
            logDevice = Devices.CreateLogDevice(logPath, true, true);
            objectLogDevice = Devices.CreateLogDevice(objectPath, true, true);

            // create the faster lookup
            lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                1L << 25,
                new LookupItemFunctions(),
                new LogSettings()
                {
                    LogDevice = logDevice,
                    ObjectLogDevice = objectLogDevice
                },
                serializerSettings: new SerializerSettings<int, LookupItem>
                {
                    valueSerializer = () => new ProtobufObjectSerializer<LookupItem>()
                },
                comparer: LookupItemFasterKeyComparer.Default);

            // start dedicated threads on the thread pool
            for (var i = 0; i < Environment.ProcessorCount; ++i)
            {
                // start a consuming task for each command queue
                var thread = threads[i] = new Thread(index => StartFasterWorkload((int)index));
                thread.Start(i);
            }

            return base.OnActivateAsync();
        }

        private void StartFasterWorkload(int index)
        {
            var queue = queues[index];
            var completion = threadCompletions[index];

            try
            {
                lookup.StartSession();
                foreach (var command in queue.GetConsumingEnumerable())
                {
                    switch (command)
                    {
                        case SetSingleCommand set:
                            {
                                var key = set.Item.Key;
                                var item = set.Item;
                                lookup.Upsert(ref key, ref item, Empty.Default, 0);
                                set.Completion.TrySetResult(set.Item);
                            }
                            break;

                        case SetRangeCommand range:
                            {
                                for (var i = 0; i < range.Items.Count; ++i)
                                {
                                    var item = range.Items[i];
                                    var key = item.Key;
                                    lookup.Upsert(ref key, ref item, Empty.Default, 0);

                                    if (i % 1024 == 0) lookup.Refresh();
                                }
                                range.Completion.TrySetResult(range.Items);
                            }
                            break;

                        default:
                            throw new InvalidOperationException();
                    }
                    lookup.Refresh();
                }
            }
            catch (Exception error)
            {
                logger.LogError(error, error.Message);
            }
            finally
            {
                try
                {
                    lookup.StopSession();
                }
                catch (Exception error)
                {
                    logger.LogError(error, error.Message);
                }
                completion.TrySetResult(true);
            }
        }

        public override async Task OnDeactivateAsync()
        {
            // tell the worker threads we are shutting down
            foreach (var queue in queues)
            {
                queue.CompleteAdding();
            }

            // wait for worker threads to completion
            await Task.WhenAll(threadCompletions.Select(_ => _.Task));

            // clean up faster resources
            lookup.Dispose();
            logDevice.Close();
            objectLogDevice.Close();

            await base.OnDeactivateAsync();
        }

        private int GetNextQueueIndex() => ++targetQueue % queues.Length;

        public Task SetAsync(LookupItem item)
        {
            var command = new SetSingleCommand(item);
            var index = GetNextQueueIndex();
            var queue = queues[index];
            queue.Add(command);
            return command.Completion.Task;
        }

        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            var command = new SetRangeCommand(items);
            var index = GetNextQueueIndex();
            var queue = queues[index];
            queue.Add(command);
            return command.Completion.Task;
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

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task<ImmutableList<LookupItem>> TryGetRangeAsync(ImmutableList<int> keys)
        {
            return Task.Run(() =>
            {
                var results = ImmutableList.CreateBuilder<LookupItem>();
                var session = Guid.Empty;
                try
                {
                    session = lookup.StartSession();
                    for (var i = 0; i < keys.Count; ++i)
                    {
                        var key = keys[i];
                        LookupItem result = null;
                        var status = lookup.Read(ref key, ref result, ref result, Empty.Default, 0);
                        switch (status)
                        {
                            case Status.OK:
                                results.Add(result);
                                break;

                            case Status.NOTFOUND:
                                break;

                            default:
                                throw new ApplicationException();
                        }
                    }
                    return Task.FromResult(results.ToImmutable());
                }
                finally
                {
                    if (session != Guid.Empty)
                    {
                        lookup.StopSession();
                    }
                }
            });
        }

        private class Command
        {
        }

        private class SetSingleCommand : Command
        {
            public LookupItem Item { get; }
            public TaskCompletionSource<LookupItem> Completion { get; } = new TaskCompletionSource<LookupItem>();

            public SetSingleCommand(LookupItem item)
            {
                Item = item;
            }
        }

        private class SetRangeCommand : Command
        {
            public ImmutableList<LookupItem> Items { get; }
            public TaskCompletionSource<ImmutableList<LookupItem>> Completion { get; } = new TaskCompletionSource<ImmutableList<LookupItem>>();

            public SetRangeCommand(ImmutableList<LookupItem> items)
            {
                Items = items;
            }
        }
    }
}