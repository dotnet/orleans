using System.CommandLine;

namespace DistributedTests.Server.Configurator
{
    public class SimpleSilo : ISiloConfigurator<object>
    {
        public string Name => nameof(SimpleSilo);

        public List<Option> Options => [];

        public void Configure(ISiloBuilder siloBuilder, object parameters)
        {
            return;
        }
    }
}
