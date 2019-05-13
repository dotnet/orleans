using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
using Orleans.Runtime;
using Orleans.Serialization.ProtobufNet;

namespace Silo
{
    public class Program
    {
        public static IHost StartNewHost()
        {
            var builder = new HostBuilder()
                .UseOrleans(_ =>
                {
                    _.UseLocalhostClustering();
                    _.ConfigureApplicationParts(m => m.AddApplicationPart(typeof(DictionaryGrain).Assembly).WithReferences());
                })
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                    _.SetMinimumLevel(LogLevel.Warning);
                    _.AddFilter("Orleans.Runtime.NoOpHostEnvironmentStatistics", LogLevel.Error);
                    _.AddFilter("Orleans.Runtime.HostedClient", LogLevel.None);
                    _.AddFilter("Orleans.Runtime.Silo", LogLevel.Error);
                    _.AddFilter("Orleans.Runtime.ClientObserverRegistrar", LogLevel.None);
                })
                .ConfigureServices(_ =>
                {
                    _.Configure<ConsoleLifetimeOptions>(x =>
                    {
                        x.SuppressStatusMessages = true;
                    });
                    _.Configure<FasterOptions>(x =>
                    {
                        x.BaseDirectory = @"C:\Temp\Faster";
                        x.HybridLogDeviceFileTitle = "hybrid.log";
                        x.ObjectLogDeviceFileTitle = "object.log";
                        x.CheckpointsSubDirectory = "checkpoints";
                    });
                    _.Configure<SerializationProviderOptions>(x =>
                    {
                        x.SerializationProviders.Add(typeof(ProtobufNetSerializer));
                    });
                    _.Configure<SiloMessagingOptions>(options =>
                    {
                        options.ResponseTimeout = TimeSpan.FromMinutes(10);
                    });
                    _.Configure<SchedulingOptions>(options =>
                    {
                        options.TurnWarningLengthThreshold = TimeSpan.FromSeconds(10);
                    });
                })
                .UseConsoleLifetime();

            var host = builder.Build();

            host.Start();

            return host;
        }

        public static void Main()
        {
            var host = StartNewHost();

            var grain = host.Services
                .GetService<IGrainFactory>()
                .GetGrain<IFasterGrain>(Guid.Empty);

            var logger = host.Services
                .GetService<ILogger<Program>>();

            var total = 1 << 20; // one million
            var batch = 1 << 10; // one thousand
            var done = 0;
            var pipeline = new AsyncPipeline(Environment.ProcessorCount);

            logger.LogWarning("Generating data...");
            var load = Enumerable.Range(0, total)
                .Select(index => new LookupItem(index, index, DateTime.UtcNow))
                .BatchIEnumerable(batch)
                .Select(list => list.ToImmutableList())
                .Select(items => Task.Run(async () =>
                {
                    try
                    {
                        await grain.SetRangeAsync(items);
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, error.Message);
                        throw;
                    }
                    Interlocked.Add(ref done, items.Count);
                }));

            logger.LogWarning("Going to load a total of {@Total:N0} items at {@BatchSize} items/batch...",
                total, batch);

            var watch = Stopwatch.StartNew();
            var timer = new Timer(_ =>
            {
                if (watch.IsRunning && watch.ElapsedMilliseconds > 0)
                {
                    logger.LogWarning("Loaded {@Done:N0} out of {@Total:N0} at {Ops:N0}/s {@Percent:N2}%",
                                done, total, ((double)done / watch.ElapsedMilliseconds) * 1000.0, (double)done / total * 100);
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            pipeline.AddRange(load);
            pipeline.Wait();

            grain.SnapshotAsync().Wait();
            watch.Stop();

            logger.LogWarning("Completed {@Items} in {@Elapsed}ms at {@Ops}/s",
                done, watch.ElapsedMilliseconds, (double)done / watch.ElapsedMilliseconds * 1000.0);

            host.StopAsync().Wait();

            //BenchmarkRunner.Run<RangeDeltaBenchmarks>();
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(config: new DebugInProcessConfig());
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run();
        }
    }
}