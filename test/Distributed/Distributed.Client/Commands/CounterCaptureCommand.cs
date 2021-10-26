using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Distributed.GrainInterfaces.Streaming;
using Microsoft.Crank.EventSources;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Distributed.Client.Commands
{
    public class CounterCaptureCommand : Command
    {
        private class Parameters
        {
            public string ServiceId { get; set; }
            public string ClusterId { get; set; }
            public SecretConfiguration.SecretSource SecretSource { get; set; }
            public string CounterKey { get; set; }
            public List<string> Counters { get; set; }
        }

        public CounterCaptureCommand()
            : base("counter", "capture the counters in parameter")
        {
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));
            AddOption(OptionHelper.CreateOption<string>("--counterKey", defaultValue: Constants.DefaultCounterGrain));
            AddArgument(new Argument<List<string>>("Counters") { Arity = ArgumentArity.OneOrMore });

            Handler = CommandHandler.Create<Parameters>(RunAsync);
        }

        private async Task RunAsync(Parameters parameters)
        {
            WriteLog("Connecting to cluster...");
            var secrets = SecretConfiguration.Load(parameters.SecretSource);
            var clientBuilder = new ClientBuilder()
                .Configure<ClusterOptions>(options => { options.ClusterId = parameters.ClusterId; options.ServiceId = parameters.ServiceId; })
                .UseAzureStorageClustering(options => options.ConnectionString = secrets.ClusteringConnectionString);
            var client = clientBuilder.Build();
            await client.Connect();

            var counterGrain = client.GetGrain<ICounterGrain>(parameters.CounterKey);

            var duration = await counterGrain.GetRunDuration();
            BenchmarksEventSource.Register("duration", Operations.First, Operations.Last, "duration", "duration", "n0");
            BenchmarksEventSource.Measure("duration", duration.TotalSeconds);

            var initialWait = await counterGrain.WaitTimeForReport();

            WriteLog($"Counters should be ready in {initialWait}");
            await Task.Delay(initialWait);

            WriteLog($"Counters ready");
            foreach (var counter in parameters.Counters)
            {
                var value = await counterGrain.GetTotalCounterValue(counter);
                WriteLog($"{counter}: {value}");
                BenchmarksEventSource.Register(counter, Operations.First, Operations.Sum, counter, counter, "n0");
                BenchmarksEventSource.Measure(counter, value);
                if (string.Compare(counter, "requests", StringComparison.InvariantCultureIgnoreCase) == 0)
                {
                    var rps = (float) value / duration.TotalSeconds;
                    BenchmarksEventSource.Register("rps", Operations.First, Operations.Last, "rps", "Requests per second", "n0");
                    BenchmarksEventSource.Measure("rps", rps);
                }
            }
        }

        private static void WriteLog(string log) => Console.WriteLine(log);
    }
}
