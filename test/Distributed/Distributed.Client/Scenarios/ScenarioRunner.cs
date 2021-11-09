using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Distributed.GrainInterfaces;
using Microsoft.Crank.EventSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Distributed.Client.Scenarios
{
    public class CommonParameters
    {
        public string ServiceId { get; set; }
        public string ClusterId { get; set; }
        public int PipelineSize { get; set; }
        public int Requests { get; set; } 
        public int Duration { get; set; }
        public SecretConfiguration.SecretSource SecretSource { get;set; }
    }

    public class ScenarioRunner<T>
    {
        const int REPORT_BLOCK_SIZE = 100000;

        private readonly IScenario<T> _scenario;

        private SemaphoreSlim _concurrencyGuard;
        private volatile int _errorCounter;
        private volatile int _requestCounter;

        public ScenarioRunner(IScenario<T> scenario)
        {
            _scenario = scenario;
        }

        public async Task Run(CommonParameters commonParameters, T scenarioParameters)
        {
            var requests = commonParameters.Requests != 0 ? commonParameters.Requests : int.MaxValue;
            var duration = commonParameters.Duration != 0 ? TimeSpan.FromSeconds(commonParameters.Duration) : TimeSpan.MaxValue;

            WriteLog("Connecting to cluster...");
            var secrets = SecretConfiguration.Load(commonParameters.SecretSource);
            var hostBuilder = new HostBuilder()
                .UseOrleansClient(builder => {
                    builder
                        .Configure<ClusterOptions>(options => { options.ClusterId = commonParameters.ClusterId; options.ServiceId = commonParameters.ServiceId; })
                        .UseAzureStorageClustering(options => options.ConfigureTableServiceClient(secrets.ClusteringConnectionString));
                });
            using var host = hostBuilder.Build();
            await host.StartAsync();

            var client = host.Services.GetService<IClusterClient>();

            WriteLog("Initializing...");
            await _scenario.Initialize(client, scenarioParameters);
            _concurrencyGuard = new SemaphoreSlim(commonParameters.PipelineSize);

            WriteLog("Starting load");
            var cts = new CancellationTokenSource(duration);
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < requests; i++)
            {
                var request = i;
                await _concurrencyGuard.WaitAsync();
                _ = IssueRequest(request);
                if (cts.IsCancellationRequested)
                {
                    break;
                }
            }
            while (_concurrencyGuard.CurrentCount != commonParameters.PipelineSize)
            {
                await Task.Delay(100);
            }
            stopwatch.Stop();
            WriteLog($"Done {_requestCounter,10:N0} requests in {stopwatch.Elapsed}");

            var rps = _requestCounter / (stopwatch.ElapsedMilliseconds / 1000.0f);
            WriteLog($"RESULTS: requests={_requestCounter} rps={rps} errors={_errorCounter}");

            BenchmarksEventSource.Register("requests", Operations.First, Operations.Sum, "Requests", "Number of requests", "n0");
            BenchmarksEventSource.Register("errors", Operations.First, Operations.Sum, "Errors", "Number of errors", "n0");
            BenchmarksEventSource.Register("rps", Operations.First, Operations.Sum, "RPS", "Requests per seconds", "n0");

            BenchmarksEventSource.Measure("requests", _requestCounter);
            BenchmarksEventSource.Measure("errors", _errorCounter);
            BenchmarksEventSource.Measure("rps", rps);

            await _scenario.Cleanup();

            await host.StopAsync();
        }

        private async Task IssueRequest(int request)
        {
            try
            {
                await _scenario.IssueRequest(request);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _errorCounter);
            }
            finally
            {
                _concurrencyGuard.Release();
                var requests = Interlocked.Increment(ref _requestCounter);
                if (requests % REPORT_BLOCK_SIZE == 0)
                {
                    WriteLog($"Done {requests,10:N0} requests ({_errorCounter,4:N0} errors)");
                }
            }
        }

        private static void WriteLog(string log) => Console.WriteLine(log);
    }
}
