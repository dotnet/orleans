using System.CommandLine;
using System.CommandLine.Invocation;
using DistributedTests.Common.MessageChannel;
using Microsoft.Extensions.Logging;

namespace DistributedTests.Client.Commands
{
    public class ChaosAgentCommand : Command
    {
        private readonly ILogger _logger;

        private class Parameters
        {
            public string ServiceId { get; set; } = null!;
            public string ClusterId { get; set; } = null!;
            public Uri AzureTableUri { get; set; } = null!;
            public Uri AzureQueueUri { get; set; } = null!;
            public int Wait { get; set; }
            public int ServersPerRound { get; set; }
            public int Rounds { get; set; }
            public int RoundDelay { get; set; }
            public bool Graceful { get; set; }
            public bool Restart { get; set; }
        }

        public ChaosAgentCommand(ILogger logger)
            : base("chaosagent", "Shutdown/restart servers gracefully or not")
        {
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption<Uri>("--azureTableUri", isRequired: true));
            AddOption(OptionHelper.CreateOption<Uri>("--azureQueueUri", isRequired: true));
            AddOption(OptionHelper.CreateOption<int>("--wait", defaultValue: 30));
            AddOption(OptionHelper.CreateOption<int>("--serversPerRound", defaultValue: 1));
            AddOption(OptionHelper.CreateOption<int>("--rounds", defaultValue: 5));
            AddOption(OptionHelper.CreateOption<int>("--roundDelay", defaultValue: 60));
            AddOption(OptionHelper.CreateOption<bool>("--graceful", defaultValue: false));
            AddOption(OptionHelper.CreateOption<bool>("--restart", defaultValue: false));

            Handler = CommandHandler.Create<Parameters, CancellationToken>(RunAsync);
            _logger = logger;
        }

        private async Task RunAsync(Parameters parameters, CancellationToken cancellationToken)
        {
            var channel = await Channels.CreateSendChannel(
                parameters.ClusterId,
                parameters.AzureQueueUri,
                cancellationToken);

            _logger.LogInformation("Waiting {WaitSeconds} seconds before starting...", parameters.Wait);
            await Task.Delay(TimeSpan.FromSeconds(parameters.Wait), cancellationToken);

            for (var i = 0; i < parameters.Rounds; i++)
            {
                _logger.LogInformation(
                    "Round #{Round}: sending {ServersPerRound} orders [Restart: {Restart}, Graceful: {Graceful}]",
                    i + 1,
                    parameters.ServersPerRound,
                    parameters.Restart,
                    parameters.Graceful);
                using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                roundCancellation.CancelAfter(TimeSpan.FromSeconds(parameters.RoundDelay));
                var responses = await channel.SendMessages(GetMessages(), roundCancellation.Token);
                _logger.LogInformation(
                    "Round #{Round}: silos {Silos} acked",
                    i + 1,
                    string.Join(",", responses.Select(r => r.ServerName)));
                _logger.LogInformation("Round #{Round}: waiting {RoundDelay}", i + 1, parameters.RoundDelay);
                await Task.Delay(TimeSpan.FromSeconds(parameters.RoundDelay), cancellationToken);
            }

            List<ServerMessage> GetMessages()
            {
                var msgs = new List<ServerMessage>();
                for (var i = 0; i < parameters.ServersPerRound; i++)
                {
                    msgs.Add(new ServerMessage(parameters.Graceful, parameters.Restart));
                }
                return msgs;
            }
        }
    }
}
