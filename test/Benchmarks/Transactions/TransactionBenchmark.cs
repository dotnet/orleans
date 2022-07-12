using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans.Hosting;
using Orleans.TestingHost;
using BenchmarkGrainInterfaces.Transaction;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Transactions;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Benchmarks.Transactions
{
    public class TransactionBenchmark : IDisposable
    {
        private TestCluster host;
        private int runs;
        private int transactionsPerRun;
        private int concurrent;

        public TransactionBenchmark(int runs, int transactionsPerRun, int concurrent)
        {
            this.runs = runs;
            this.transactionsPerRun = transactionsPerRun;
            this.concurrent = concurrent;
        }

        public void MemorySetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void MemoryThrottledSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionThrottlingConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloAzureStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureThrottledSetup()
        {
            var builder = new TestClusterBuilder(4);
            builder.AddSiloBuilderConfigurator<SiloAzureStorageConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloTransactionThrottlingConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public class SiloMemoryStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }

        public class SiloAzureStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureTableTransactionalStateStorageAsDefault(options =>
                {
                    options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                });
            }
        }

        public class SiloTransactionThrottlingConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<TransactionRateLoadSheddingOptions>(options =>
                {
                    options.Enabled = true;
                    options.Limit = 50;
                });
            }
        }

        public async Task RunAsync()
        {
            Console.WriteLine($"Cold Run.");
            await FullRunAsync();
            for(int i=0; i<runs; i++)
            {
                Console.WriteLine($"Warm Run {i+1}.");
                await FullRunAsync();
            }
        }

        private async Task FullRunAsync()
        {
            int runners = Math.Max(1,(int)Math.Sqrt(concurrent));
            int transactionsPerRunner = Math.Max(1, this.transactionsPerRun / runners);
            Report[] reports = await Task.WhenAll(Enumerable.Range(0, runners).Select(i => RunAsync(i, transactionsPerRunner, runners)));
            Report finalReport = new Report();
            foreach (Report report in reports)
            {
                finalReport.Succeeded += report.Succeeded;
                finalReport.Failed += report.Failed;
                finalReport.Throttled += report.Throttled;
                finalReport.Elapsed = TimeSpan.FromMilliseconds(Math.Max(finalReport.Elapsed.TotalMilliseconds, report.Elapsed.TotalMilliseconds));
            }
            Console.WriteLine($"{finalReport.Succeeded} transactions in {finalReport.Elapsed.TotalMilliseconds}ms.");
            Console.WriteLine($"{(int)(finalReport.Succeeded * 1000 / finalReport.Elapsed.TotalMilliseconds)} transactions per second.");
            Console.WriteLine($"{finalReport.Failed} transactions failed.");
            Console.WriteLine($"{finalReport.Throttled} transactions were throttled.");
        }

        public async Task<Report> RunAsync(int run, int transactiosPerRun, int concurrentPerRun)
        {
            ILoadGrain load = this.host.Client.GetGrain<ILoadGrain>(Guid.NewGuid());
            await load.Generate(run, transactiosPerRun, concurrentPerRun);
            Report report = null;
            while (report == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                report = await load.TryGetReport();
            }
            return report;
        }

        public void Teardown()
        {
            host.StopAllSilos();
        }

        public void Dispose()
        {
            host?.Dispose();
        }

        public sealed class SiloTransactionConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseTransactions();
            }
        }
    }
}