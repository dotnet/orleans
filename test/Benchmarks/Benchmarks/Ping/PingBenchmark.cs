using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.TestingHost;
using BenchmarkGrainInterfaces.Ping;
using Microsoft.Extensions.Configuration;
using TestExtensions;

namespace Benchmarks.Ping
{
    public class PingBenchmark
    {
        private TestCluster host;

        public void Setup()
        {
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.ConfigureLegacyConfiguration();

            this.host = builder.Build();
            this.host.Deploy();
        }

        public async Task RunAsync()
        {
            Console.WriteLine($"Cold Run.");
            await FullRunAsync();
            Console.WriteLine($"Warm Run.");
            await FullRunAsync();
        }

        private async Task FullRunAsync()
        {
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => RunAsync(i, 100, TimeSpan.FromSeconds(30))));
            Report finalReport = new Report();
            foreach (Report report in reports)
            {
                finalReport.Succeeded += report.Succeeded;
                finalReport.Failed += report.Failed;
                finalReport.Elapsed = TimeSpan.FromMilliseconds(Math.Max(finalReport.Elapsed.TotalMilliseconds, report.Elapsed.TotalMilliseconds));
            }
            Console.WriteLine($"{finalReport.Succeeded} calls in {finalReport.Elapsed.TotalMilliseconds}ms.");
            Console.WriteLine($"{finalReport.Succeeded * 1000 / finalReport.Elapsed.TotalMilliseconds} calls per second.");
            Console.WriteLine($"{finalReport.Failed} calls failed.");
        }

        public async Task<Report> RunAsync(int run, int concurrentPerRun, TimeSpan duration)
        {
            ILoadGrain load = this.host.Client.GetGrain<ILoadGrain>(Guid.NewGuid());
            await load.Generate(run, concurrentPerRun, duration);
            Report report = null;
            while (report == null)
            {
                await Task.Delay(1000);
                report = await load.TryGetReport();
            }
            return report;
        }

        public void Teardown()
        {
            this.host.StopAllSilos();
        }
    }
}