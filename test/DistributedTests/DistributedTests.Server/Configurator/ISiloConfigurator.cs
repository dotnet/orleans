using System.CommandLine;

namespace DistributedTests.Server.Configurator
{
    public interface ISiloConfigurator<T>
    {
        string Name { get; }

        List<Option> Options { get; }

        void Configure(ISiloBuilder siloBuilder, T parameters);
    }
}
