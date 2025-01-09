using Azure.Identity;
using DistributedTests.Common;
using Microsoft.Crank.EventSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace DistributedTests.Client.LoadGeneratorScenario
{
    public class ClientParameters
    {
        public string ServiceId { get; set; }
        public string ClusterId { get; set; }
        public int ConnectionsPerEndpoint { get; set; }
        public Uri AzureTableUri { get; set; }
        public Uri AzureQueueUri { get; set; }
    }

    public class LoadGeneratorParameters
    {
        public int NumWorkers { get; set; }
        public int BlocksPerWorker { get; set; }
        public int RequestsPerBlock { get; set; } 
        public int Duration { get; set; }
    }

    public class LoadGeneratorScenarioRunner<T>
    {
        private readonly ILoadGeneratorScenario<T> _scenario;
        private readonly ILogger _logger;

        public LoadGeneratorScenarioRunner(ILoadGeneratorScenario<T> scenario, ILoggerFactory loggerFactory)
        {
            _scenario = scenario;
            _logger = loggerFactory.CreateLogger(scenario.Name);
        }

        public async Task Run(ClientParameters clientParams, LoadGeneratorParameters loadParams)
        {
            Console.WriteLine($"AzureTableUri: {clientParams.AzureTableUri}");

            // Register the measurements. n0 -> format as natural number
            BenchmarksEventSource.Register("requests", Operations.Sum, Operations.Sum, "Requests", "Number of requests completed", "n0");
            BenchmarksEventSource.Register("failures", Operations.Sum, Operations.Sum, "Failures", "Number of failures", "n0");
            BenchmarksEventSource.Register("rps", Operations.Sum, Operations.Median, "Median RPS", "Rate per second", "n0");

            var hostBuilder = new HostBuilder().UseOrleansClient((ctx, builder) =>
                builder.Configure<ClusterOptions>(options => { options.ClusterId = clientParams.ClusterId; options.ServiceId = clientParams.ServiceId; })
                       .Configure<ConnectionOptions>(options => clientParams.ConnectionsPerEndpoint = 2)
                       .UseAzureStorageClustering(options => options.TableServiceClient = clientParams.AzureTableUri.CreateTableServiceClient()));
            using var host = hostBuilder.Build();

            _logger.LogInformation("Connecting to cluster...");
            await host.StartAsync();
            var client = host.Services.GetService<IClusterClient>();

            var generator = new ConcurrentLoadGenerator<T>(
                numWorkers: loadParams.NumWorkers,
                blocksPerWorker: loadParams.BlocksPerWorker != 0 ? loadParams.BlocksPerWorker : int.MaxValue,
                requestsPerBlock: loadParams.RequestsPerBlock,
                issueRequest: _scenario.IssueRequest,
                getStateForWorker: workerId => _scenario.GetStateForWorker(client, workerId),
                logger: _logger,
                logIntermediateResults: true);

            _logger.LogInformation("Warming-up...");
            await generator.Warmup();

            var cts = loadParams.Duration != 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(loadParams.Duration))
                : new CancellationTokenSource();

            _logger.LogInformation("Running");
            var report = await generator.Run(cts.Token);

            BenchmarksEventSource.Register("overall-rps", Operations.Last, Operations.Last, "Overall RPS", "RPS", "n0");
            BenchmarksEventSource.Measure("overall-rps", report.RatePerSecond);

            await host.StopAsync();
        }
    }
}
