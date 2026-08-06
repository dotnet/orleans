using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Tests;

public sealed class ClusterFixtureWithConfig : IDisposable
{
    public TestCluster Cluster { get; } = new TestClusterBuilder()
        .AddSiloBuilderConfigurator<TestSiloConfigurations>()
        .Build();

    public ClusterFixtureWithConfig() => Cluster.Deploy();

    void IDisposable.Dispose() => Cluster.StopAllSilos();
}

file sealed class TestSiloConfigurations : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        // TODO: Call required service registrations here.
        // siloBuilder.Services.AddSingleton<T, Impl>(/* ... */);
    }
}
