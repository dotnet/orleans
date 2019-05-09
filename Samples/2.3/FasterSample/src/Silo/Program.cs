using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
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
        private static IHost host;
        private static ILogger<Program> logger;

        public static IHost BuildHost()
        {
            return new HostBuilder()
                .UseOrleans(_ =>
                {
                    _.UseLocalhostClustering();
                    _.ConfigureApplicationParts(m => m.AddApplicationPart(typeof(VolatileLookupGrain).Assembly).WithReferences());
                })
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                    _.AddFilter((category, level) =>
                        category.StartsWith("Orleans.") || category.StartsWith("Runtime.")
                        ? level >= LogLevel.Warning
                        : level >= LogLevel.Information);
                    _.SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices(_ =>
                {
                    _.Configure<ConsoleLifetimeOptions>(x =>
                    {
                        x.SuppressStatusMessages = true;
                    });
                    _.Configure<FasterOptions>(x =>
                    {
                        x.HybridLogDeviceBaseDirectory = @"C:\Temp\Faster";
                        x.HybridLogDeviceFileTitle = @"hybrid.log";
                        x.ObjectLogDeviceBaseDirectory = @"C:\Temp\Faster";
                        x.ObjectLogDeviceFileTitle = @"object.log";
                        x.CheckpointBaseDirectory = @"C:\Temp\faster-checkpoints";
                        x.CheckpointContainerDirectory = @"checkpoints";
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
        }

        public static void Main()
        {
            BenchmarkRunner.Run<SequentialBenchmarks>();
        }

        public static async Task TestSequentialUpsertsOnDictionaryGrainAsync()
        {
            logger.LogInformation("Running sequential dictionary upsert test...");

            // wake up the grain
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.NewGuid());
            await grain.StartAsync();

            // prepare test data
            var items = Enumerable.Range(1, 100000).Select(x => new LookupItem(x % 10, x % 10, DateTime.UtcNow)).ToImmutableList();

            // perform test
            var watch = Stopwatch.StartNew();
            foreach (var item in items)
            {
                await grain.SetAsync(item);
            }
            watch.Stop();

            logger.LogInformation("Dictionary upserts: {@Count} items in {@ElapsedMs}ms @ {@Rate}/s",
                items.Count, watch.ElapsedMilliseconds, items.Count / watch.Elapsed.TotalSeconds);

            await grain.StopAsync();
        }

        public static async Task TestReentrantUpsertsDictionaryGrainAsync()
        {
            logger.LogInformation("Running reentrant upsert test on dictionary grain...");

            // wake up the grain
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.NewGuid());
            await grain.StartAsync();

            // prepare test data
            var items = Enumerable.Range(1, 100000).Select(x => new LookupItem(x % 10, x % 10, DateTime.UtcNow)).ToImmutableList();

            // perform test
            var watch = Stopwatch.StartNew();
            var tasks = new Task[items.Count];
            for (var i = 0; i < items.Count; ++i)
            {
                tasks[i] = grain.SetAsync(items[i]);
            }
            await Task.WhenAll(tasks);
            watch.Stop();

            logger.LogInformation("Performance: {@Count} items in {@ElapsedMs}ms @ {@Rate}/s",
                items.Count, watch.ElapsedMilliseconds, items.Count / watch.Elapsed.TotalSeconds);

            await grain.StopAsync();
        }

        public static async Task TestSequentialMassUpsertsOnDictionaryGrainAsync()
        {
            logger.LogInformation("Running sequential mass upsert test on dictionary grain...");

            // wake up the grain
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<IDictionaryLookupGrain>(Guid.NewGuid());
            await grain.StartAsync();

            // prepare test data
            var items = Enumerable.Range(1, 100000).Select(x => new LookupItem(x % 10, x % 10, DateTime.UtcNow)).ToImmutableList();

            // perform test
            var watch = Stopwatch.StartNew();
            foreach (var item in items)
            {
                await grain.SetAsync(item);
            }
            watch.Stop();

            logger.LogInformation("Performance: {@Count} items in {@ElapsedMs}ms @ {@Rate}/s",
                items.Count, watch.ElapsedMilliseconds, items.Count / watch.Elapsed.TotalSeconds);

            await grain.StopAsync();
        }

        public static async Task TestSequentialUpserts()
        {
            // wake up the grain
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<IVolatileLookupGrain>("Sequential");
            await grain.StartAsync();

            // prepare test data
            var items = Enumerable.Range(1, 100000).Select(x => new LookupItem(x, x, DateTime.UtcNow)).ToImmutableList();

            // perform test
            var watch = Stopwatch.StartNew();
            foreach (var item in items)
            {
                await grain.SetAsync(item);
            }
            watch.Stop();

            logger.LogInformation("Sequential Upserts: {@Count} items in {@ElapsedMs}ms @ {@Rate}/ms",
                items.Count, watch.ElapsedMilliseconds, (double)items.Count / (double)watch.ElapsedMilliseconds);
        }

        public static async Task TestReentrantUpserts()
        {
            // wake up the grain
            var grain = host.Services.GetService<IGrainFactory>().GetGrain<IVolatileLookupGrain>("Reentrant");
            await grain.StartAsync();

            // prepare test data
            var items = Enumerable.Range(1, 1000000).Select(x => new LookupItem(x, x, DateTime.UtcNow)).ToImmutableList();

            // perform test
            var watch = Stopwatch.StartNew();
            var tasks = new Task[items.Count];
            for (var i = 0; i < items.Count; ++i)
            {
                tasks[i] = grain.SetAsync(items[i]);
            }
            await Task.WhenAll(tasks);
            watch.Stop();

            logger.LogInformation("Reentrant Upserts: {@Count} items in {@ElapsedMs}ms @ {@Rate}/ms",
                items.Count, watch.ElapsedMilliseconds, (double)items.Count / (double)watch.ElapsedMilliseconds);
        }
    }
}