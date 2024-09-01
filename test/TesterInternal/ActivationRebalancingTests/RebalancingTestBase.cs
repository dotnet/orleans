/*
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement.Rebalancing;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class ActivationRebalancingTests : BaseTestClusterFixture, IClassFixture<ActivationRebalancingTests.Fixture>
{
    private readonly SiloAddress _silo1;
    private readonly SiloAddress _silo2;
    private readonly IActivationRebalancerGrain _grain;
    private readonly IActivationRebalancingController _controller;

    public ActivationRebalancingTests(Fixture fixture)
    {
        var silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();

        _silo1 = silos[0];
        _silo2 = silos[1];

        _grain = GrainFactory.GetGrain<IActivationRebalancerGrain>(0);
        _controller = fixture.HostedCluster.ServiceProvider.GetRequiredService<IActivationRebalancingController>();
    }

    [Fact]
    public void Run()
    {

    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
#pragma warning disable ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                => hostBuilder
                    .Configure<SiloMessagingOptions>(o =>
                    {
                        o.AssumeHomogenousSilosForTesting = true;
                        o.ClientGatewayShutdownNotificationTimeout = default;
                    })
                    .Configure<ActivationRebalancerOptions>(o =>
                    {
                    })
                    .AddActivationRebalancer();
#pragma warning restore ORLEANSEXP002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }
    }
}
*/