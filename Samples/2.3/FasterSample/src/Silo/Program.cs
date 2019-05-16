using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Configs;
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

namespace Silo
{
    public class Program
    {
        public static IHost BuildHost() => new HostBuilder()

            .UseOrleans(_ => _
                .UseLocalhostClustering()
                .ConfigureApplicationParts(apm => apm.AddApplicationPart(typeof(FasterGrain).Assembly).WithReferences())
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

        public static async Task Main()
        {
#if DEBUG
            BenchmarkRunner.Run<VolatileBatchWriteBenchmarks>(new DebugInProcessConfig());
#else
            BenchmarkRunner.Run<VolatileBatchWriteBenchmarks>();
#endif
        }
    }
}