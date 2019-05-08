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
using Serilog;

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
                    _.AddSerilog(new LoggerConfiguration()
                        .WriteTo.Console()
                        .CreateLogger());
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
                    _.Configure<SiloMessagingOptions>(options =>
                    {
                        options.ResponseTimeout = TimeSpan.FromMinutes(10);
                    });
                })
                .UseConsoleLifetime()
                .Build();

            await host.StartAsync();

            // test helpers
            int count = 10;
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<ILookupGrain>("SomeShardingKey");
            var logger = host.Services.GetService<ILogger<Program>>();

            await grain.StartAsync();

            // attempt a number of individual inserts
            /*
            Watch.Restart();
            count = 10;
            for (var i = 0; i < count; ++i)
            {
                await grain.SetAsync(new LookupItem(i, i, DateTime.UtcNow));
            }
            Watch.Stop();
            logger.LogInformation("Added {@Count} individual items in {@ElapsedMs}ms",
                count, Watch.ElapsedMilliseconds);
            */

            // perform a single batch of inserts
            Watch.Restart();
            count = 1000000;

            logger.LogInformation("Creating {@Count} items...", count);
            var builder = ImmutableList.CreateBuilder<LookupItem>();
            for (var i = 0; i < count; ++i)
            {
                builder.Add(new LookupItem(i, i, DateTime.UtcNow));
            }
            var items = builder.ToImmutable();
            logger.LogInformation("Created {@Count} items in {@ElapsedMs}ms", count, Watch.ElapsedMilliseconds);

            Watch.Restart();
            logger.LogInformation("Adding {@Count} items as a batch...", count);
            await grain.SetAsync(items);
            logger.LogInformation("Added {@Count} items as a batch in {@ElapsedMs}ms",
                count, Watch.ElapsedMilliseconds);


            await host.WaitForShutdownAsync();
        }
    }
}