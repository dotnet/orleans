using System.Collections.Immutable;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    public class LookupGrain : Grain, ILookupGrain
    {
        private readonly ILogger<LookupGrain> logger;
        private readonly LookupOptions options;
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        private string GrainType => nameof(LookupGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public LookupGrain(ILogger<LookupGrain> logger, IOptions<LookupOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        public override async Task OnActivateAsync()
        {
            await Task.Run(() =>
            {
                // define the underlying log file
                logDevice = Devices.CreateLogDevice(options.FasterHybridLogDevicePath);
                objectLogDevice = Devices.CreateLogDevice(options.FasterObjectLogDevicePath);

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
                        CheckpointDir = options.FasterCheckpointDirectory
                    },
                    serializerSettings: new SerializerSettings<int, LookupItem>
                    {
                        valueSerializer = () => new ProtobufObjectSerializer<LookupItem>()
                    },
                    comparer: LookupItemFasterKeyComparer.Default);
            });

            await base.OnActivateAsync();
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
            await Task.Run(() =>
            {
                try
                {
                    lookup.StartSession();
                    foreach (var item in items)
                    {
                        var xKey = item.Key;
                        var xItem = item;
                        lookup.Upsert(ref xKey, ref xItem, Empty.Default, 0);
                    }
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

        public Task<LookupItem> GetAsync(int key)
        {
            return Task.FromResult<LookupItem>(null);
        }

        public Task StartAsync() => Task.CompletedTask;
    }
}