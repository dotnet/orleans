using System.Collections.Generic;
using System.CommandLine;
using Orleans.Hosting;

namespace DistributedTests.Server.Configurator
{
    public interface ISiloConfigurator<T>
    {
        string Name { get; }

        List<Option> Options { get; }

        void Configure(ISiloBuilder siloBuilder, T parameters);
    }
}
