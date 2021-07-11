using System;
using BenchmarkDotNet.Running;
using Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Silo
{
    public class Program
    {
        public static IHost BuildHost() => new HostBuilder()

            .UseOrleans(_ => _
                .UseLocalhostClustering()
                .ConfigureApplicationParts(apm => apm.AddApplicationPart(typeof(FasterThreadPoolGrain).Assembly).WithReferences())
                .ConfigureLogging(lb => lb.SetMinimumLevel(LogLevel.Warning)))

            .ConfigureLogging(_ => _.AddConsole())

            .ConfigureServices(_ => _
                .Configure<ConsoleLifetimeOptions>(x => x.SuppressStatusMessages = true)
                .Configure<SchedulingOptions>(x => x.TurnWarningLengthThreshold = TimeSpan.FromSeconds(10))
                .Configure<FasterOptions>(x =>
                {
                    x.BaseDirectory = @"C:\Temp\Faster";
                    x.HybridLogDeviceFileTitle = "hybrid.log";
                    x.ObjectLogDeviceFileTitle = "object.log";
                    x.CheckpointsSubDirectory = "checkpoints";
                }))

            .UseConsoleLifetime()
            .Build();

        public static void Main()
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(
            //config: new BenchmarkDotNet.Configs.DebugInProcessConfig()
            );
        }
    }
}