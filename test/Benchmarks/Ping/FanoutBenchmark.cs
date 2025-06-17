using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Orleans.Configuration;

namespace Benchmarks.Ping
{
    /// <summary>
    /// Benchmarks grain communication with fanout patterns across multiple silos.
    /// </summary>
    [MemoryDiagnoser]
    public class FanoutBenchmark : IDisposable
    {
        private readonly ConsoleCancelEventHandler _onCancelEvent;
        private readonly List<IHost> hosts = new();
        private readonly ITreeGrain grain;
        private readonly IClusterClient client;
        private readonly IHost clientHost;

        public FanoutBenchmark() : this(2, true) { }

        public FanoutBenchmark(int numSilos, bool startClient, bool grainsOnSecondariesOnly = false)
        {
            for (var i = 0; i < numSilos; ++i)
            {
                var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
                var hostBuilder = new HostBuilder().UseOrleans((ctx, siloBuilder) =>
                {
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                    siloBuilder.AddActivationRepartitioner();
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                    siloBuilder.ConfigureLogging(l =>
                    {
                        l.AddSimpleConsole(o =>
                        {
                            o.UseUtcTimestamp = true;
                            o.TimestampFormat = "HH:mm:ss ";
                            o.ColorBehavior = LoggerColorBehavior.Enabled;
                        });
                        l.AddFilter("Orleans.Runtime.Placement.Repartitioning", LogLevel.Debug);
                    });
                    siloBuilder.Configure<ActivationRepartitionerOptions>(o =>
                    {
                    });
                    siloBuilder.UseLocalhostClustering(
                        siloPort: 11111 + i,
                        gatewayPort: 30000 + i,
                        primarySiloEndpoint: primary);

                    if (i == 0 && grainsOnSecondariesOnly)
                    {
                        siloBuilder.Configure<GrainTypeOptions>(options => options.Classes.Remove(typeof(PingGrain)));
                    }
                });

                var host = hostBuilder.Build();

                host.StartAsync().GetAwaiter().GetResult();
                this.hosts.Add(host);
            }

            if (grainsOnSecondariesOnly) Thread.Sleep(4000);

            if (startClient)
            {
                var hostBuilder = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
                {
                    if (numSilos == 1)
                    {
                        clientBuilder.UseLocalhostClustering();
                    }
                    else
                    {
                        var gateways = Enumerable.Range(30000, numSilos).Select(i => new IPEndPoint(IPAddress.Loopback, i)).ToArray();
                        clientBuilder.UseStaticClustering(gateways);
                    }
                });

                this.clientHost = hostBuilder.Build();
                this.clientHost.StartAsync().GetAwaiter().GetResult();

                this.client = this.clientHost.Services.GetRequiredService<IClusterClient>();
                var grainFactory = this.client;

                this.grain = grainFactory.GetGrain<ITreeGrain>(0, keyExtension: "0");
                this.grain.Ping().AsTask().GetAwaiter().GetResult();
            }

            _onCancelEvent = CancelPressed;
            Console.CancelKeyPress += _onCancelEvent;
            AppDomain.CurrentDomain.FirstChanceException += (object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e) => Console.WriteLine("FIRST CHANCE EXCEPTION: " + LogFormatter.PrintException(e.Exception));
            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) => Console.WriteLine("UNHANDLED EXCEPTION: " + LogFormatter.PrintException((Exception)e.ExceptionObject));
        }

        private void CancelPressed(object sender, ConsoleCancelEventArgs e)
        {
            Environment.Exit(0);
        }

        [Benchmark]
        public ValueTask Ping() => grain.Ping();

        public async Task PingForever()
        {
            while (true)
            {
                await grain.Ping();
            }
        }

        public async Task Shutdown()
        {
            if (clientHost is { } client)
            {
                await client.StopAsync();
                if (client is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    client.Dispose();
                }
            }

            this.hosts.Reverse();
            foreach (var host in this.hosts)
            {
                await host.StopAsync();
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
        }

        [GlobalCleanup]
        public void Dispose()
        {
            (this.client as IDisposable)?.Dispose();
            this.hosts.ForEach(h => h.Dispose());

            Console.CancelKeyPress -= _onCancelEvent;
        }
    }
}
