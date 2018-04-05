using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkGrainInterfaces.Ping;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    public class SequentialPingBenchmarkConfig : ManualConfig
    {
        public SequentialPingBenchmarkConfig()
        {
            Add(Job.ShortRun);
            Add(new MemoryDiagnoser());
        }
    }

    [Config(typeof(SequentialPingBenchmarkConfig))]
    public class SequentialPingBenchmark : IDisposable 
    {
        private readonly ISiloHost host;
        private readonly IPingGrain grain;
        private readonly IClusterClient client;

        public SequentialPingBenchmark()
        {
            this.host = new SiloHostBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.host.StartAsync().GetAwaiter().GetResult();

            this.client = new ClientBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.client.Connect().GetAwaiter().GetResult();
            
            this.grain = this.client.GetGrain<IPingGrain>(Guid.NewGuid().GetHashCode());
            this.grain.Run().GetAwaiter().GetResult();
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

        public void Dispose()
        {
            this.client.Dispose();
            this.host.Dispose();
        }
    }
}