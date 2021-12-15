using System.CommandLine;
using System.CommandLine.Invocation;
using DistributedTests;
using DistributedTests.Server.Configurator;
using Microsoft.Extensions.Hosting;

namespace DistributedTests.Server
{
    public class ServerCommand<T> : Command
    {
        private readonly ServerRunner<T> _siloRunner;

        public ServerCommand(ISiloConfigurator<T> siloConfigurator)
            : base(siloConfigurator.Name)
        {
            _siloRunner = new ServerRunner<T>(siloConfigurator);

            AddOption(OptionHelper.CreateOption<string>("--serviceId", isRequired: true));
            AddOption(OptionHelper.CreateOption<string>("--clusterId", isRequired: true));
            AddOption(OptionHelper.CreateOption("--siloPort", defaultValue: 11111));
            AddOption(OptionHelper.CreateOption("--gatewayPort", defaultValue: 30000));
            AddOption(OptionHelper.CreateOption("--secretSource", defaultValue: SecretConfiguration.SecretSource.File));

            foreach (var opt in siloConfigurator.Options)
            {
                AddOption(opt);
            }

            Handler = CommandHandler.Create<CommonParameters, T>(_siloRunner.Run);
        }
    }

    public static class Server
    {
        public static Command CreateCommand<T>(ISiloConfigurator<T> configurator) => new ServerCommand<T>(configurator);
    }
}
