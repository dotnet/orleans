using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.Development;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using BenchmarkGrainInterfaces.Transaction;

namespace Benchmarks.Transactions
{
    public class TransactionBenchmark
    {
        private TestCluster _host;

        public void Setup()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider();
            options.UseSiloBuilderFactory<SiloBuilderFactory>();
            _host = new TestCluster(options);
            _host.Deploy();
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
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => RunAsync(i, 5000, 300)));
            Report finalReport = new Report();
            foreach (Report report in reports)
            {
                finalReport.Succeeded += report.Succeeded;
                finalReport.Failed += report.Failed;
                finalReport.Elapsed = TimeSpan.FromMilliseconds(Math.Max(finalReport.Elapsed.TotalMilliseconds, report.Elapsed.TotalMilliseconds));
            }
            Console.WriteLine($"{finalReport.Succeeded} transactions in {finalReport.Elapsed.TotalMilliseconds}ms.");
            Console.WriteLine($"{finalReport.Succeeded * 1000 / finalReport.Elapsed.TotalMilliseconds} transactions per second.");
            Console.WriteLine($"{finalReport.Failed} transactions failed.");
        }

        private Task WarmUpAsync()
        {
            return Task.WhenAll(Enumerable.Range(0, 100).Select(i => RunAsync(i, 100, 10)));
        }

        public async Task<Report> RunAsync(int run, int transactiosPerRun, int concurrentPerRun)
        {
            ILoadGrain load = this._host.Client.GetGrain<ILoadGrain>(Guid.NewGuid());
            await load.Generate(run, transactiosPerRun, concurrentPerRun);
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
            _host.StopAllSilos();
        }

        public sealed class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder().ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)))
                    .UseInClusterTransactionManager()
                    .UseInMemoryTransactionLog()
                    .UseTransactionalState();
            }
        }
    }

}