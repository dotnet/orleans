using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkGrainInterfaces.Ping;
using BenchmarkGrains.Ping;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    [MemoryDiagnoser]
    public class PingBenchmark : IDisposable 
    {
        private readonly List<ISiloHost> hosts = new List<ISiloHost>();
        private readonly IPingGrain grain;
        private readonly IClusterClient client;

        public PingBenchmark() : this(1, true) { }

        public PingBenchmark(int numSilos, bool startClient, bool grainsOnSecondariesOnly = false)
        {
            for (var i = 0; i < numSilos; ++i)
            {
                var primary = i == 0 ? null : new IPEndPoint(IPAddress.Loopback, 11111);
                var siloBuilder = new SiloHostBuilder()
                    .ConfigureDefaults()
                    .UseLocalhostClustering(
                        siloPort: 11111 + i,
                        gatewayPort: 30000 + i,
                        primarySiloEndpoint: primary);

                if (i == 0 && grainsOnSecondariesOnly)
                {
                    siloBuilder.ConfigureApplicationParts(parts =>
                        parts.AddApplicationPart(typeof(IPingGrain).Assembly));
                    siloBuilder.ConfigureServices(services =>
                    {
                        services.Remove(services.First(s => s.ImplementationType?.Name == "ApplicationPartValidator"));
                    });
                }
                else
                {
                    siloBuilder.ConfigureApplicationParts(parts =>
                        parts.AddApplicationPart(typeof(IPingGrain).Assembly)
                             .AddApplicationPart(typeof(PingGrain).Assembly));
                }

                var silo = siloBuilder.Build();
                silo.StartAsync().GetAwaiter().GetResult();
                this.hosts.Add(silo);
            }

            if (grainsOnSecondariesOnly) Thread.Sleep(4000);

            if (startClient)
            {
                var clientBuilder = new ClientBuilder()
                    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IPingGrain).Assembly))
                    .Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev");

                if (numSilos == 1)
                {
                    clientBuilder.UseLocalhostClustering();
                }
                else
                {
                    var gateways = Enumerable.Range(30000, numSilos).Select(i => new IPEndPoint(IPAddress.Loopback, i)).ToArray();
                    clientBuilder.UseStaticClustering(gateways);
                }

                this.client = clientBuilder.Build();
                this.client.Connect().GetAwaiter().GetResult();
                var grainFactory = this.client;

                this.grain = grainFactory.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
                this.grain.Run().GetAwaiter().GetResult();
            }
        }
        
        [Benchmark]
        public Task Ping() => grain.Run();

        public async Task PingForever()
        {
            while (true)
            {
                await grain.Run();
            }
        }

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
            if (this.client is IClusterClient c)
            {
                await c.Close();
                c.Dispose();
            }

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
        }
    }
}