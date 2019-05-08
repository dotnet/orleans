using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Grains;
using Grains.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization.ProtobufNet;

namespace Silo
{
    public class Program
    {
        private static readonly Stopwatch Watch = new Stopwatch();

        public static async Task Main()
        {
            var host = new HostBuilder()
                .UseOrleans(_ =>
                {
                    _.UseLocalhostClustering();
                    _.ConfigureApplicationParts(m => m.AddApplicationPart(typeof(LookupGrain).Assembly).WithReferences());
                })
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                })
                .ConfigureServices(_ =>
                {
                    _.Configure<ConsoleLifetimeOptions>(x =>
                    {
                        x.SuppressStatusMessages = true;
                    });
                    _.Configure<LookupOptions>(x =>
                    {
                        x.FasterHybridLogDevicePath = @"C:\temp\faster-hybrid.log";
                        x.FasterObjectLogDevicePath = @"C:\temp\faster-object.log";
                        x.FasterCheckpointDirectory = @"C:\temp\faster-checkpoints";
                    });
                    _.Configure<SerializationProviderOptions>(x =>
                    {
                        x.SerializationProviders.Add(typeof(ProtobufNetSerializer));
                    });
                })
                .UseConsoleLifetime()
                .Build();

            await host.StartAsync();

            // test helpers
            var count = 100;
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<ILookupGrain>("SomeShardingKey");
            var logger = host.Services.GetService<ILogger<Program>>();

            await grain.StartAsync();

            // attempt a number of individual inserts
            Watch.Restart();
            for (var i = 0; i < count; ++i)
            {
                await grain.SetAsync(new LookupItem(i, i, DateTime.UtcNow));
            }
            Watch.Stop();
            logger.LogInformation("Added {@Count} individual items in {@ElapsedMs}ms",
                count, Watch.ElapsedMilliseconds);

            // perform a single batch of inserts
            Watch.Restart();
            var builder = ImmutableList.CreateBuilder<LookupItem>();
            for (var i = 0; i < count; ++i)
            {
                builder.Add(new LookupItem(i, i, DateTime.UtcNow));
            }
            await grain.SetAsync(builder.ToImmutable());
            Watch.Stop();
            logger.LogInformation("Added {@Count} items as a batch in {@ElapsedMs}ms",
                count, Watch.ElapsedMilliseconds);



            await host.WaitForShutdownAsync();
        }
    }
}