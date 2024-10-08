using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace ChaoticCluster.Silo;

class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
#pragma warning disable ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
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
