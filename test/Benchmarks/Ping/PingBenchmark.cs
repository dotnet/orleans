using System;
using System.Threading.Tasks;
using System.Linq;
using BenchmarkGrainInterfaces.Ping;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Benchmarks.Ping
{
    public class PingBenchmark
    {
        private ISiloHost host;
        private IClusterClient client;

        public void Setup()
        {
            this.host = new SiloHostBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.host.StartAsync().GetAwaiter().GetResult();

            this.client = new ClientBuilder().UseLocalhostClustering().Configure<ClusterOptions>(options => options.ClusterId = options.ServiceId = "dev").Build();
            this.client.Connect().GetAwaiter().GetResult();
        }

        public async Task RunAsync()
        {
            Console.WriteLine($"Cold Run - 10000 concurrent.");
            await FullRunAsync(10000);
            Console.WriteLine($"Warm Run - 100 concurrent.");
            await FullRunAsync(100);
            Console.WriteLine($"Warm Run - 1000 concurrent.");
            await FullRunAsync(1000);
            Console.WriteLine($"Warm Run - 10000 concurrent.");
            await FullRunAsync(10000);
        }

        private async Task FullRunAsync(int concurrent)
        {
            const int runners = 10;
            int concurrentPerRunner = concurrent / runners;
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => RunAsync(i, concurrentPerRunner, TimeSpan.FromSeconds(15))));
            Report finalReport = new Report();
            foreach (Report report in reports)
            {
                finalReport.Succeeded += report.Succeeded;
                finalReport.Failed += report.Failed;
                finalReport.Elapsed = TimeSpan.FromMilliseconds(Math.Max(finalReport.Elapsed.TotalMilliseconds, report.Elapsed.TotalMilliseconds));
            }
            Console.WriteLine($"{finalReport.Succeeded} calls in {finalReport.Elapsed.TotalMilliseconds}ms.");
            Console.WriteLine($"{(int)(finalReport.Succeeded * 1000 / finalReport.Elapsed.TotalMilliseconds)} calls per second.");
            Console.WriteLine($"{finalReport.Failed} calls failed.");
        }

        public async Task<Report> RunAsync(int run, int concurrentPerRun, TimeSpan duration)
        {
            ILoadGrain load = this.client.GetGrain<ILoadGrain>(Guid.NewGuid());
            await load.Generate(run, concurrentPerRun);
            await Task.Delay(duration);
            return await load.TryGetReport();
        }

        public void Teardown()
        {
            this.client.Dispose();
            this.host.Dispose();
        }
    }
}