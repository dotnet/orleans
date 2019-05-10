using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Grains;
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

        public static void Main() => BenchmarkRunner.Run<Benchmarks>();
    }
}