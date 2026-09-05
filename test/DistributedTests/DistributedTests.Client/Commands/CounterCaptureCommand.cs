using System.CommandLine;
using System.CommandLine.Invocation;
using DistributedTests.Common;
using DistributedTests.GrainInterfaces;
using Microsoft.Crank.EventSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace DistributedTests.Client.Commands
{
    public class CounterCaptureCommand : Command
    {
        private readonly ILogger _logger;

        private class Parameters
        {
            public string ServiceId { get; set; } = null!;
            public string ClusterId { get; set; } = null!;
            public Uri AzureTableUri { get; set; } = null!;
            public Uri AzureQueueUri { get; set; } = null!;
            public string CounterKey { get; set; } = null!;
            public List<string> Counters { get; set; } = null!;
        }

        public CounterCaptureCommand(ILogger logger)
            : base("counter", "capture the counters in parameter")
        {
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption<Uri>("--azureTableUri", isRequired: true));
            AddOption(OptionHelper.CreateOption<Uri>("--azureQueueUri", isRequired: true));
            AddOption(OptionHelper.CreateOption("--counterKey", defaultValue: StreamingConstants.DefaultCounterGrain));
            AddArgument(new Argument<List<string>>("Counters") { Arity = ArgumentArity.OneOrMore });

            Handler = CommandHandler.Create<Parameters, CancellationToken>(RunAsync);
            _logger = logger;
        }

        private async Task RunAsync(Parameters parameters, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Connecting to cluster...");
            var hostBuilder = new HostBuilder()
                .UseOrleansClient((ctx, builder) =>
                {
                    builder
                        .Configure<ClusterOptions>(options => { options.ClusterId = parameters.ClusterId; options.ServiceId = parameters.ServiceId; })
                        .UseAzureStorageClustering(options => options.TableServiceClient = parameters.AzureTableUri.CreateTableServiceClient());
                });
            using var host = hostBuilder.Build();
            await host.StartAsync(cancellationToken);

            // The Orleans client is always registered by UseOrleansClient above.
            var client = host.Services.GetService<IClusterClient>()!;

            var counterGrain = client.GetGrain<ICounterGrain>(parameters.CounterKey);

            var duration = await counterGrain.GetRunDuration(cancellationToken);
            BenchmarksEventSource.Register("duration", Operations.First, Operations.Last, "duration", "duration", "n0");
            BenchmarksEventSource.Measure("duration", duration.TotalSeconds);

            var initialWait = await counterGrain.WaitTimeForReport(cancellationToken);

            _logger.LogInformation("Counters should be ready in {InitialWait}", initialWait);
            await Task.Delay(initialWait, cancellationToken);

            _logger.LogInformation("Counters ready");
            foreach (var counter in parameters.Counters)
            {
                var value = await counterGrain.GetTotalCounterValue(counter, cancellationToken);
                _logger.LogInformation("{Counter}: {Value}", counter, value);
                BenchmarksEventSource.Register(counter, Operations.First, Operations.Sum, counter, counter, "n0");
                BenchmarksEventSource.Measure(counter, value);
                if (string.Equals(counter, "requests", StringComparison.OrdinalIgnoreCase))
                {
                    var rps = (float)value / duration.TotalSeconds;
                    BenchmarksEventSource.Register("rps", Operations.First, Operations.Last, "rps", "Requests per second", "n0");
                    BenchmarksEventSource.Measure("rps", rps);
                }
            }

            await host.StopAsync(cancellationToken);
        }
    }
}
