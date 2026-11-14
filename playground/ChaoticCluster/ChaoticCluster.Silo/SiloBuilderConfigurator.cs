using Orleans.TestingHost;

namespace ChaoticCluster.Silo;

class SiloBuilderConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddDistributedGrainDirectory();
    }
}

internal interface IMyTestGrain : IGrainWithIntegerKey
{
    ValueTask Ping();
}

[CollectionAgeLimit(Minutes = 1.01)]
internal class MyTestGrain : Grain, IMyTestGrain
{
    public ValueTask Ping() => default;
}
