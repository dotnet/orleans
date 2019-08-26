using System;
using System.Threading.Tasks;
using System.Linq;
using BenchmarkGrainInterfaces.Ping;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.ActivationData
{
    public class MemoryBenchmark
    {
        private ISiloHost host;

        public void Setup()
        {
            this.host = new SiloHostBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.host.StartAsync().GetAwaiter().GetResult();
        }

        public async Task RunAsync()
        {
            var grainFactory = this.host.Services.GetRequiredService<IGrainFactory>();

            for (var i = 0; i < 100000; i++)
            {
                var grain = grainFactory.GetGrain<IPingGrain>(i);

                await grain.Run();
            }

            Console.WriteLine("All grains created. Waiting 10 sec");

            await Task.Delay(10000);
        }

        public void Teardown()
        {
            this.host.Dispose();
        }
    }
}