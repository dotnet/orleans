using System.CommandLine;
using System.CommandLine.Invocation;
using DistributedTests.Client.LoadGeneratorScenario;
using Microsoft.Extensions.Logging;

namespace DistributedTests.Client.Commands
{
    public class ScenarioCommand<T> : Command
    {
        private readonly LoadGeneratorScenarioRunner<T> _runner;

        public ScenarioCommand(ILoadGeneratorScenario<T> scenario, ILoggerFactory loggerFactory)
            : base(scenario.Name)
        {
            _runner = new LoadGeneratorScenarioRunner<T>(scenario, loggerFactory);

            // ClientParameters
            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption<int>("--connectionsPerEndpoint", defaultValue: 1, validator: OptionHelper.OnlyStrictlyPositive));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));

            // LoadGeneratorParameters
            AddOption(OptionHelper.CreateOption<int>("--numWorkers", defaultValue: 250, validator: OptionHelper.OnlyStrictlyPositive));
            AddOption(OptionHelper.CreateOption<int>("--blocksPerWorker", defaultValue: 10));
            AddOption(OptionHelper.CreateOption<int>("--requestsPerBlock", defaultValue: 500, validator: OptionHelper.OnlyStrictlyPositive));
            AddOption(OptionHelper.CreateOption<int>("--duration", defaultValue: 0, validator: OptionHelper.OnlyPositiveOrZero));

            Handler = CommandHandler.Create<ClientParameters, LoadGeneratorParameters>(_runner.Run);
        }
    }

    public static class Scenario
    {
        public static Command CreateCommand<T>(ILoadGeneratorScenario<T> scenario, ILoggerFactory loggerFactory) => new ScenarioCommand<T>(scenario, loggerFactory);
    }
}
