using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.Hosting;
using Orleans.TestingHost;
using BenchmarkGrainInterfaces.Transaction;
using TestExtensions;

namespace Benchmarks.Transactions
{
    public class TransactionBenchmark
    {
        private TestCluster host;

        public void Setup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureSetup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloAzureStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public class SiloMemoryStorageConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }

        public class SiloAzureStorageConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddAzureTableTransactionalStateStorageAsDefault(options =>
                {
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                });
            }
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
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => RunAsync(i, 2000, 500)));
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

        public sealed class SiloTransactionConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.UseDistributedTM();
            }
        }
    }

}