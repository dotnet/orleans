using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement.Rebalancing;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class GenericRebalancingTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{
    private readonly RebalancerFixture _fixture = fixture;

    [Fact]
    public void Should_Do_Reporting()
    {
        var rebalancer = _fixture.HostedCluster.ServiceProvider.GetRequiredService<IActivationRebalancer>();
        var listener = new StatusListener();
        rebalancer.SubscribeToStatusChanges(listener);

        Assert.False(listener.HasStarted, false);
    }

    private class StatusListener : IActivationRebalancerReportListener
    {
        public bool HasStarted { get; private set; }
        public bool HasStopped { get; private set; }

        public void OnStarted() => HasStarted = true;
        public void OnStopped(TimeSpan? duration) => throw new NotImplementedException();
    }
}
