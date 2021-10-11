using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    [MemoryDiagnoser]
    public class PingBenchmark : IDisposable 
    {
        private readonly ConsoleCancelEventHandler _onCancelEvent;
        private readonly List<IHost> hosts = new List<IHost>();
        private readonly IPingGrain grain;
        private readonly IClusterClient client;
        private readonly IHost clientHost;

        public PingBenchmark() : this(1, true) { }

        public PingBenchmark(int numSilos, bool startClient, bool grainsOnSecondariesOnly = false)
        {
            for (var i = 0; i < numSilos; ++i)
            {
                var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
                var hostBuilder = new HostBuilder().UseOrleans(siloBuilder =>
                {
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
                var hostBuilder = new HostBuilder().UseOrleansClient(clientBuilder =>
                {
                    clientBuilder.Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev");

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

                this.grain = grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
                this.grain.Run().AsTask().GetAwaiter().GetResult();
            }

            _onCancelEvent = CancelPressed;
            Console.CancelKeyPress += _onCancelEvent;
        }

        private void CancelPressed(object sender, ConsoleCancelEventArgs e)
        {
            Environment.Exit(0);
        }

        [Benchmark]
        public ValueTask Ping() => grain.Run();

        public async Task PingForever()
        {
            while (true)
            {
                await grain.Run();
            }
        }

        public Task PingConcurrentForever() => this.Run(
            runs: int.MaxValue,
            grainFactory: this.client,
            blocksPerWorker: 10);

        public Task PingConcurrent() => this.Run(
            runs: 3,
            grainFactory: this.client,
            blocksPerWorker: 10);

        public Task PingConcurrentHostedClient(int blocksPerWorker = 30) => this.Run(
            runs: 3,
            grainFactory: (IGrainFactory)this.hosts[0].Services.GetService(typeof(IGrainFactory)),
            blocksPerWorker: blocksPerWorker);

        private async Task Run(int runs, IGrainFactory grainFactory, int blocksPerWorker)
        {
            var loadGenerator = new ConcurrentLoadGenerator<IPingGrain>(
                maxConcurrency: 250,
                blocksPerWorker: blocksPerWorker,
                requestsPerBlock: 500,
                issueRequest: g => g.Run(),
                getStateForWorker: workerId => grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode()));
            await loadGenerator.Warmup();
            while (runs-- > 0) await loadGenerator.Run();
        }

        public async Task PingPongForever()
        {
            var other = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            while (true)
            {
                await grain.PingPongInterleave(other, 100);
            }
        }

        public async Task Shutdown()
        {
            await this.clientHost.StopAsync();
            if (clientHost is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else clientHost.Dispose();

            this.hosts.Reverse();
            foreach (var h in this.hosts)
            {
                await h.StopAsync();
                h.Dispose();
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