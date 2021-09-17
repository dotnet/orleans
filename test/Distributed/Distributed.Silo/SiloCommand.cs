using System.CommandLine;
using System.CommandLine.Invocation;
using Distributed.GrainInterfaces;
using Distributed.Silo.Configurator;
using Microsoft.Extensions.Hosting;

namespace Distributed.Silo
{
    public class SiloCommand<T> : Command
    {
        private readonly SiloRunner<T> _siloRunner;

        public SiloCommand(ISiloConfigurator<T> siloConfigurator)
            : base(siloConfigurator.Name)
        {
            _siloRunner = new SiloRunner<T>(siloConfigurator);

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

    public static class Silo
    {
        public static Command CreateCommand<T>(ISiloConfigurator<T> configurator) => new SiloCommand<T>(configurator);
    }
}
