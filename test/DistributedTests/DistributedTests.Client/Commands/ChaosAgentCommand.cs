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

        public ChaosAgentCommand(ILogger logger)
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
            _logger = logger;
        }

        private async Task RunAsync(Parameters parameters)
        {
            var secrets = SecretConfiguration.Load(parameters.SecretSource);
            var channel = await Channels.CreateSendChannel(parameters.ClusterId, secrets);

            _logger.LogInformation("Waiting {WaitSeconds} seconds before starting...", parameters.Wait);
            await Task.Delay(TimeSpan.FromSeconds(parameters.Wait));

            for (var i=0; i<parameters.Rounds; i++)
            {
                _logger.LogInformation(
                    "Round #{Round}: sending {ServersPerRound} orders [Restart: {Restart}, Graceful: {Graceful}]",
                    i + 1,
                    parameters.ServersPerRound,
                    parameters.Restart,
                    parameters.Graceful);
                var responses = await channel.SendMessages(
                    GetMessages(),
                    new CancellationTokenSource(TimeSpan.FromSeconds(parameters.RoundDelay)).Token);
                _logger.LogInformation(
                    "Round #{Round}: silos {Silos} acked",
                    i + 1,
                    string.Join(",", responses.Select(r => r.ServerName)));
                _logger.LogInformation("Round #{Round}: waiting {RoundDelay}", i + 1, parameters.RoundDelay);
                await Task.Delay(TimeSpan.FromSeconds(parameters.RoundDelay));
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
