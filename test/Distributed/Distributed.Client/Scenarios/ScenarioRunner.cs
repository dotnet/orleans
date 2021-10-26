using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Distributed.GrainInterfaces;
using Microsoft.Crank.EventSources;
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
            WriteLog("Connecting to cluster...");
            var secrets = SecretConfiguration.Load(commonParameters.SecretSource);
            var clientBuilder = new ClientBuilder()
                .Configure<ClusterOptions>(options => { options.ClusterId = commonParameters.ClusterId; options.ServiceId = commonParameters.ServiceId; })
                .UseAzureStorageClustering(options => options.ConnectionString = secrets.ClusteringConnectionString);
            var client = clientBuilder.Build();
            await client.Connect();

            WriteLog("Initializing...");
            await _scenario.Initialize(client, scenarioParameters);
            _concurrencyGuard = new SemaphoreSlim(commonParameters.PipelineSize);

            WriteLog("Starting load");
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < commonParameters.Requests; i++)
            {
                var request = i;
                await _concurrencyGuard.WaitAsync();
                _ = IssueRequest(request);
            }
            while (_concurrencyGuard.CurrentCount != commonParameters.PipelineSize)
            {
                await Task.Delay(100);
            }
            stopwatch.Stop();
            WriteLog($"Done {commonParameters.Requests,10:N0} requests in {stopwatch.Elapsed}");

            var rps = commonParameters.Requests / (stopwatch.ElapsedMilliseconds / 1000.0f);
            WriteLog($"RESULTS: requests={commonParameters.Requests} rps={rps} errors={_errorCounter}");

            BenchmarksEventSource.Register("requests", Operations.First, Operations.Sum, "Requests", "Number of requests", "n0");
            BenchmarksEventSource.Register("errors", Operations.First, Operations.Sum, "Errors", "Number of errors", "n0");
            BenchmarksEventSource.Register("rps", Operations.First, Operations.Sum, "RPS", "Requests per seconds", "n0");

            BenchmarksEventSource.Measure("requests", commonParameters.Requests);
            BenchmarksEventSource.Measure("errors", _errorCounter);
            BenchmarksEventSource.Measure("rps", rps);

            await _scenario.Cleanup();
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
