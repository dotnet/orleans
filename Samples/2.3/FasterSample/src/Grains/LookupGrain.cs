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
    public class LookupGrain : Grain, ILookupGrain
    {
        private readonly ILogger<LookupGrain> logger;
        private readonly LookupOptions options;
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        private string GrainType => nameof(LookupGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        private readonly BlockingCollection<WorkItem> queue = new BlockingCollection<WorkItem>();
        private Thread thread;

        public LookupGrain(ILogger<LookupGrain> logger, IOptions<LookupOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override async Task OnActivateAsync()
        {
            thread = new Thread(() =>
            {
                // define the underlying log file
                logDevice = Devices.CreateLogDevice(options.FasterHybridLogDevicePath, preallocateFile: false, deleteOnClose: true);
                objectLogDevice = Devices.CreateLogDevice(options.FasterObjectLogDevicePath, preallocateFile: false, deleteOnClose: true);

                // create the faster lookup
                lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                    1L << 20,
                    new LookupItemFunctions(),
                    new LogSettings()
                    {
                        LogDevice = logDevice,
                        ObjectLogDevice = objectLogDevice,
                    },
                    new CheckpointSettings
                    {
                        CheckpointDir = options.FasterCheckpointDirectory,
                        CheckPointType = CheckpointType.Snapshot
                    },
                    serializerSettings: new SerializerSettings<int, LookupItem>
                    {
                        valueSerializer = () => new ProtobufObjectSerializer<LookupItem>()
                    },
                    comparer: LookupItemFasterKeyComparer.Default);

                // attempt to recover
                if (Directory.Exists(options.FasterCheckpointDirectory) && Directory.EnumerateFiles(options.FasterCheckpointDirectory).Any())
                {
                    lookup.Recover();
                }

                try
                {
                    lookup.StartSession();
                    var serial = 0;
                    foreach (var workItem in queue.GetConsumingEnumerable())
                    {
                        foreach (var item in workItem.LookupItem)
                        {
                            var xKey = item.Key;
                            var xItem = item;
                            lookup.Upsert(ref xKey, ref xItem, Empty.Default, ++serial);
                        }
                        lookup.TakeFullCheckpoint(out var token);
                        lookup.CompleteCheckpoint(true);
                        workItem.Completion.TrySetResult(true);
                    }
                }
                finally
                {
                    lookup.StopSession();
                }
            });
            thread.Start();

            await base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            queue.CompleteAdding();

            return base.OnDeactivateAsync();
        }

        public async Task SetAsync(LookupItem item)
        {
            var key = item.Key;

            await Task.Run(() =>
            {
                try
                {
                    lookup.StartSession();
                    lookup.Upsert(ref key, ref item, Empty.Default, 0);
                    lookup.CompletePending(true);
                    lookup.TakeHybridLogCheckpoint(out var token);
                    lookup.CompleteCheckpoint(true);
                }
                finally
                {
                    lookup.StopSession();
                }
            });
        }

        public async Task SetAsync(ImmutableList<LookupItem> items)
        {
            logger.LogInformation("Faster is adding {@Count} items as a batch...", items.Count);

            var watch = Stopwatch.StartNew();

            var workItem = new WorkItem(items);
            queue.Add(workItem);
            await workItem.Completion.Task;

            logger.LogInformation("Faster added {@Count} items as a batch in {@ElapsedMs}ms", items.Count, watch.ElapsedMilliseconds);

            /*
            logger.LogInformation("Faster is completing pending operations...");

            watch = Stopwatch.StartNew();

            logger.LogInformation("Faster completed pending operations in {@ElapsedMs}ms", watch.ElapsedMilliseconds);

            logger.LogInformation("Faster is taking a checkpoint...");
            watch = Stopwatch.StartNew();
            lookup.TakeFullCheckpoint(out var token);
            lookup.CompleteCheckpoint(true);
            logger.LogInformation("Faster completed the checkpoint in {@ElapsedMs}ms", watch.ElapsedMilliseconds);
            */
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