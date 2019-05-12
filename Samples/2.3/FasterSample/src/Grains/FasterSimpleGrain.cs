using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class FasterSimpleGrain : Grain, IFasterSimpleGrain
    {
        private readonly FasterOptions options;
        private IDevice logDevice;
        private IDevice objectLogDevice;
        private FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;
        private SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

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
                1L << 21,
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

        public async Task SetAsync(LookupItem item)
        {
            await semaphore.WaitAsync();

            try
            {
                lookup.StartSession();

                var key = item.Key;
                lookup.Upsert(ref key, ref item, Empty.Default, 0);
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
                        var item = items[i];
                        var key = item.Key;
                        lookup.Upsert(ref key, ref item, Empty.Default, i);
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
    }
}