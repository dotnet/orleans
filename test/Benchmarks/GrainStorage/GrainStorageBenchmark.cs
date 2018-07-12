using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using BenchmarkGrainInterfaces.GrainStorage;

namespace Benchmarks.GrainStorage
{
    public class GrainStorageBenchmark
    {
        private TestCluster host;

        public void MemorySetup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloMemoryStorageConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureTableSetup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloAzureTableStorageConfigurator>();
            this.host = builder.Build();
            this.host.Deploy();
        }

        public void AzureBlobSetup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloAzureBlobStorageConfigurator>();
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

        public class SiloAzureTableStorageConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddAzureTableGrainStorageAsDefault(options =>
                {
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                });
            }
        }

        public class SiloAzureBlobStorageConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorageAsDefault(options =>
                {
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                });
            }
        }

        public async Task RunAsync()
        {
            bool running = true;
            Func<bool> isRunning = () => running;
            Task[] tasks = { PersistLoop(isRunning), Task.Delay(TimeSpan.FromSeconds(30)) };
            await Task.WhenAny(tasks);
            running = false;
            await Task.WhenAll(tasks);
        }

        public async Task PersistLoop(Func<bool> running)
        {
            IPersistentGrain persistentGrain = this.host.Client.GetGrain<IPersistentGrain>(Guid.NewGuid());
            // activate grain
            await persistentGrain.Set(0);
            int stored = 0;
            int failed = 0;
            TimeSpan maxCalltime = TimeSpan.MinValue;
            Stopwatch sw = Stopwatch.StartNew();
            Stopwatch calltime = new Stopwatch();
            while (running())
            {
                calltime.Restart();
                try
                {
                    await persistentGrain.Set(stored);
                    stored++;
                    calltime.Stop();
                }
                catch (Exception)
                {
                    failed++;
                    calltime.Stop();
                }
                maxCalltime = maxCalltime < calltime.Elapsed ? calltime.Elapsed : maxCalltime;
            }
            sw.Stop();
            Console.WriteLine($"Performed {stored} persist operations with {failed} failures in {sw.ElapsedMilliseconds}ms.");
            Console.WriteLine($"Average time in ms per call was {sw.ElapsedMilliseconds/stored}, with longest call taking {maxCalltime.TotalMilliseconds}ms.");
        }

        public void Teardown()
        {
            host.StopAllSilos();
        }
    }
}
