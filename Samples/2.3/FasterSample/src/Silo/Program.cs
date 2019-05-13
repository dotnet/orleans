using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
                        x.BaseDirectory = @"D:\Temp\Faster";
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

            var total = 1 << 30;
            var batch = 1 << 20;
            var done = 0;
            var pipeline = new AsyncPipeline(Environment.ProcessorCount);
            var generator = Enumerable.Range(0, total)
                .Select(index => new LookupItem(index, index, DateTime.UtcNow))
                .BatchIEnumerable(batch)
                .Select(list => list.ToImmutableList())
                .Select(items => grain.SetRangeAsync(items, true).ContinueWith(_ =>
                {
                    Interlocked.Add(ref done, batch);
                    Console.WriteLine($"Loaded {done} items...");
                }));

            pipeline.AddRange(generator);
            pipeline.Wait();

            Console.WriteLine("Complete");

            host.StopAsync().Wait();

            //BenchmarkRunner.Run<RangeDeltaBenchmarks>();
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(config: new DebugInProcessConfig());
            //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run();
        }
    }
}