using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Options;
using Orleans;

namespace Grains
{
    public class FasterSimpleGrain : Grain, IFasterSimpleGrain
    {
        private readonly FasterOptions options;
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        public FasterSimpleGrain(IOptions<FasterOptions> options)
        {
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
                1L << 20,
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

            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            lookup.Dispose();
            logDevice.Close();
            objectLogDevice.Close();

            return base.OnDeactivateAsync();
        }

        public Task SetAsync(LookupItem item)
        {
            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();

                var key = item.Key;
                lookup.Upsert(ref key, ref item, Empty.Default, 0);
                lookup.Refresh();
            }
            finally
            {
                if (session != Guid.Empty)
                {
                    lookup.StopSession();
                }
            }
            return Task.CompletedTask;
        }

        public Task SetAsync(ImmutableList<LookupItem> items) => Task.CompletedTask;

        public Task<LookupItem> GetAsync(int key) => Task.FromResult<LookupItem>(null);

        public Task StartAsync() => Task.CompletedTask;

        public Task StopAsync()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}