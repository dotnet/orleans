using System.Diagnostics;
using Orleans.TestingHost;
using TestExtensions;
using BenchmarkGrainInterfaces.GrainStorage;

namespace Benchmarks.GrainStorage
{
    public class GrainStorageBenchmark : IDisposable
    {
        private TestCluster host;
        private readonly int concurrent;
        private readonly int payloadSize;
        private readonly TimeSpan duration;

        public GrainStorageBenchmark(int concurrent, int payloadSize, TimeSpan duration)
        {
            this.concurrent = concurrent;
            this.payloadSize = payloadSize;
            this.duration = duration;
        }

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

        public void AdoNetSetup()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloAdoNetStorageConfigurator>();
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

        public class SiloAzureTableStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureTableGrainStorageAsDefault(options =>
                {
                    options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                });
            }
        }

        public class SiloAzureBlobStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorageAsDefault(options =>
                {
                    options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataConnectionString);
                });
            }
        }

        public class SiloAdoNetStorageConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAdoNetGrainStorageAsDefault(options =>
                {
                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                });
            }
        }

        public async Task RunAsync()
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool running = true;
            bool isRunning() => running;
            var runTask = Task.WhenAll(Enumerable.Range(0, concurrent).Select(i => RunAsync(i, isRunning)).ToList());
            Task[] waitTasks = { runTask, Task.Delay(duration) };
            await Task.WhenAny(waitTasks);
            running = false;
            var runResults = await runTask;
            sw.Stop(); 
            var reports = runResults.SelectMany(r => r).ToList();

            var stored = reports.Count(r => r.Success);
            var failed = reports.Count(r => !r.Success);
            var calltimes = reports.Select(r => r.Elapsed.TotalMilliseconds);
            var calltime = calltimes.Sum();
            var maxCalltime = calltimes.Max();
            var averageCalltime = calltimes.Average();
            Console.WriteLine($"Performed {stored} persist (read & write) operations with {failed} failures in {sw.ElapsedMilliseconds}ms.");
            Console.WriteLine($"Average time in ms per call was {averageCalltime}, with longest call taking {maxCalltime}ms.");
            Console.WriteLine($"Total time waiting for the persistent store was {calltime}ms.");
        }

        public async Task<List<Report>> RunAsync(int instance, Func<bool> running)
        {
            var persistentGrain = this.host.Client.GetGrain<IPersistentGrain>(Guid.NewGuid());
            // activate grain
            await persistentGrain.Init(payloadSize);
            var iteration = instance % payloadSize;
            var reports = new List<Report>(5000);
            while (running())
            {
                var report = await persistentGrain.TrySet(iteration);
                reports.Add(report);
                iteration = (iteration + 1) % payloadSize;
            }

            return reports;
        }

        public void Teardown()
        {
            host.StopAllSilos();
        }

        public void Dispose()
        {
            host?.Dispose();
        }
    }
}
