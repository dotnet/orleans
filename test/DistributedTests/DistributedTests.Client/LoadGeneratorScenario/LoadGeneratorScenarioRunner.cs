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
        public string ServiceId { get; set; } = null!;
        public string ClusterId { get; set; } = null!;
        public int ConnectionsPerEndpoint { get; set; }
        public Uri AzureTableUri { get; set; } = null!;
        public Uri AzureQueueUri { get; set; } = null!;
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

        public async Task Run(
            ClientParameters clientParams,
            LoadGeneratorParameters loadParams,
            CancellationToken cancellationToken)
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
            await host.StartAsync(cancellationToken);
            // The Orleans client is always registered by UseOrleansClient above.
            var client = host.Services.GetService<IClusterClient>()!;

            var generator = new ConcurrentLoadGenerator<T>(
                numWorkers: loadParams.NumWorkers,
                blocksPerWorker: loadParams.BlocksPerWorker != 0 ? loadParams.BlocksPerWorker : int.MaxValue,
                requestsPerBlock: loadParams.RequestsPerBlock,
                issueRequest: _scenario.IssueRequest,
                getStateForWorker: workerId => _scenario.GetStateForWorker(client, workerId),
                logger: _logger,
                logIntermediateResults: true);

            _logger.LogInformation("Warming-up...");
            await generator.Warmup(cancellationToken);

            using var durationCancellation = new CancellationTokenSource();
            if (loadParams.Duration != 0)
            {
                durationCancellation.CancelAfter(TimeSpan.FromSeconds(loadParams.Duration));
            }

            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                durationCancellation.Token);
            _logger.LogInformation("Running");
            var report = await generator.Run(runCancellation.Token, cancellationToken);

            BenchmarksEventSource.Register("overall-rps", Operations.Last, Operations.Last, "Overall RPS", "RPS", "n0");
            BenchmarksEventSource.Measure("overall-rps", report.RatePerSecond);

            await host.StopAsync(cancellationToken);
        }
    }
}
