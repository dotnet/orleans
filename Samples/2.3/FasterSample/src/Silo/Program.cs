using System;
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
        public static IHost BuildHost() =>

            new HostBuilder()
                .UseOrleans(_ =>
                {
                    _.UseLocalhostClustering();
                    _.ConfigureApplicationParts(m => m.AddApplicationPart(typeof(DictionaryGrain).Assembly).WithReferences());
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
                })
                .UseConsoleLifetime()
                .Build();

        public static void Main() => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run();
    }
}