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
        public SecretConfiguration.SecretSource SecretSource { get;set; }
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
            var secrets = SecretConfiguration.Load(clientParams.SecretSource);
            var hostBuilder = new HostBuilder().UseOrleansClient((ctx, builder) =>
                builder.Configure<ClusterOptions>(options => { options.ClusterId = clientParams.ClusterId; options.ServiceId = clientParams.ServiceId; })
                       .Configure<ConnectionOptions>(options => clientParams.ConnectionsPerEndpoint = 2)
                       .UseAzureStorageClustering(options => options.ConfigureTableServiceClient(secrets.ClusteringConnectionString)));
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

            // Register the measurements. n0 -> format as natural number
            BenchmarksEventSource.Register("requests", Operations.First, Operations.Sum, "Requests", "Number of requests completed", "n0");
            BenchmarksEventSource.Register("failures", Operations.First, Operations.Sum, "Failures", "Number of failures", "n0");
            BenchmarksEventSource.Register("rps", Operations.First, Operations.Sum, "Rate per second", "Rate per seconds", "n0");

            // Register the measurement values
            BenchmarksEventSource.Measure("requests", report.Completed);
            BenchmarksEventSource.Measure("failures", report.Failures);
            BenchmarksEventSource.Measure("rps", report.RatePerSecond);

            await host.StopAsync();
        }
    }
}
