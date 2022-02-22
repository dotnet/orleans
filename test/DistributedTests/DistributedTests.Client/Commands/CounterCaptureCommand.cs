using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using DistributedTests.GrainInterfaces;
using Microsoft.Crank.EventSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace DistributedTests.Client.Commands
{
    public class CounterCaptureCommand : Command
    {
        private readonly ILogger _logger;

        private class Parameters
        {
            public string ServiceId { get; set; }
            public string ClusterId { get; set; }
            public SecretConfiguration.SecretSource SecretSource { get; set; }
            public string CounterKey { get; set; }
            public List<string> Counters { get; set; }
        }

        public CounterCaptureCommand(ILogger logger)
            : base("counter", "capture the counters in parameter")
        {
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));
            AddOption(OptionHelper.CreateOption<string>("--counterKey", defaultValue: StreamingConstants.DefaultCounterGrain));
            AddArgument(new Argument<List<string>>("Counters") { Arity = ArgumentArity.OneOrMore });

            Handler = CommandHandler.Create<Parameters>(RunAsync);
            _logger = logger;
        }

        private async Task RunAsync(Parameters parameters)
        {
            _logger.LogInformation("Connecting to cluster...");
            var secrets = SecretConfiguration.Load(parameters.SecretSource);
            var hostBuilder = new HostBuilder()
                .UseOrleansClient((ctx, builder) => {
                    builder
                        .Configure<ClusterOptions>(options => { options.ClusterId = parameters.ClusterId; options.ServiceId = parameters.ServiceId; })
                        .UseAzureStorageClustering(options => options.ConfigureTableServiceClient(secrets.ClusteringConnectionString));
                });
            using var host = hostBuilder.Build();
            await host.StartAsync();

            var client = host.Services.GetService<IClusterClient>();

            var counterGrain = client.GetGrain<ICounterGrain>(parameters.CounterKey);

            var duration = await counterGrain.GetRunDuration();
            BenchmarksEventSource.Register("duration", Operations.First, Operations.Last, "duration", "duration", "n0");
            BenchmarksEventSource.Measure("duration", duration.TotalSeconds);

            var initialWait = await counterGrain.WaitTimeForReport();

            _logger.LogInformation($"Counters should be ready in {initialWait}");
            await Task.Delay(initialWait);

            _logger.LogInformation($"Counters ready");
            foreach (var counter in parameters.Counters)
            {
                var value = await counterGrain.GetTotalCounterValue(counter);
                _logger.LogInformation($"{counter}: {value}");
                BenchmarksEventSource.Register(counter, Operations.First, Operations.Sum, counter, counter, "n0");
                BenchmarksEventSource.Measure(counter, value);
                if (string.Equals(counter, "requests", StringComparison.InvariantCultureIgnoreCase))
                {
                    var rps = (float) value / duration.TotalSeconds;
                    BenchmarksEventSource.Register("rps", Operations.First, Operations.Last, "rps", "Requests per second", "n0");
                    BenchmarksEventSource.Measure("rps", rps);
                }
            }

            await host.StopAsync();
        }
    }
}
