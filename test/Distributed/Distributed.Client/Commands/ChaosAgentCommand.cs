using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Distributed.Common;
using Distributed.GrainInterfaces.Streaming;
using Microsoft.Crank.EventSources;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Distributed.Client.Commands
{
    public class ChaosAgentCommand : Command
    {
        private class Parameters
        {
            public string ServiceId { get; set; }
            public string ClusterId { get; set; }
            public SecretConfiguration.SecretSource SecretSource { get; set; }
            public int Wait { get; set; }
            public int ServersPerRound { get; set; }
            public int Rounds { get; set; }
            public int RoundDelay { get; set; }
            public bool Graceful { get; set; }
            public bool Restart { get; set; }
        }

        public ChaosAgentCommand()
            : base("chaosagent", "Shutdown/restart servers gracefully or not")
        {
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));
            AddOption(OptionHelper.CreateOption<int>("--wait", defaultValue: 30));
            AddOption(OptionHelper.CreateOption<int>("--serversPerRound", defaultValue: 1));
            AddOption(OptionHelper.CreateOption<int>("--rounds", defaultValue: 5));
            AddOption(OptionHelper.CreateOption<int>("--roundDelay", defaultValue: 60));
            AddOption(OptionHelper.CreateOption<bool>("--graceful", defaultValue: false));
            AddOption(OptionHelper.CreateOption<bool>("--restart", defaultValue: false));

            Handler = CommandHandler.Create<Parameters>(RunAsync);
        }

        private async Task RunAsync(Parameters parameters)
        {
            var secrets = SecretConfiguration.Load(parameters.SecretSource);
            var channel = await Channels.CreateSendChannel(parameters.ClusterId, secrets);

            WriteLog($"Waiting {parameters.Wait} seconds before starting...");
            await Task.Delay(TimeSpan.FromSeconds(parameters.Wait));

            for (var i=0; i<parameters.Rounds; i++)
            {
                WriteLog($"Round #{i + 1}: sending {parameters.ServersPerRound} orders [Restart: {parameters.Restart}, Graceful: {parameters.Graceful}]");
                var responses = await channel.SendMessages(
                    GetMessages(),
                    new CancellationTokenSource(TimeSpan.FromSeconds(parameters.RoundDelay)).Token);
                WriteLog($"Round #{i + 1}: silos {string.Join(",", responses.Select(r => r.SiloName))} acked");
                WriteLog($"Round #{i + 1}: waiting {parameters.RoundDelay}");
                await Task.Delay(TimeSpan.FromSeconds(parameters.RoundDelay));
            }

            List<SiloMessage> GetMessages()
            {
                var msgs = new List<SiloMessage>();
                for (var i = 0; i < parameters.ServersPerRound; i++)
                {
                    msgs.Add(new SiloMessage(parameters.Graceful, parameters.Restart));
                }
                return msgs;
            }
        }

        private static void WriteLog(string log) => Console.WriteLine(log);
    }
}
