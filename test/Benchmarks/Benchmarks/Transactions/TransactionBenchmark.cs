using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.Development;
using Orleans.TestingHost;
using BenchmarkGrainInterfaces.Transaction;

namespace Benchmarks.Transactions
{
    public class TransactionBenchmark
    {
        private TestCluster host;

        public void Setup()
        {
            var builder = new TestClusterBuilder();
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider();
            });
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
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
            ILoadGrain load = this.host.Client.GetGrain<ILoadGrain>(Guid.NewGuid());
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
            host.StopAllSilos();
        }

        public sealed class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .UseInClusterTransactionManager()
                    .UseInMemoryTransactionLog()
                    .UseTransactionalState();
            }
        }
    }

}