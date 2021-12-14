using System.Collections.Generic;
using System.CommandLine;
using Orleans.Hosting;

namespace DistributedTests.Server.Configurator
{
    public class SimpleSilo : ISiloConfigurator<object>
    {
        public string Name => nameof(SimpleSilo);

        public List<Option> Options => new();

        public void Configure(ISiloBuilder siloBuilder, object parameters)
        {
            return;
        }
    }
}
