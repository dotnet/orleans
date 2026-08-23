using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class ActivationWorkSignalRuntimeTests : TestClusterPerTest
{
    [Fact]
    public async Task IdleActivationDeactivationCompletesAndReactivates()
    {
        var grain = GrainFactory.GetGrain<ICatalogTestGrain>(Random.Shared.NextInt64());
        var initialActivationId = await grain.GetActivationId();

        await HostedCluster.DeactivateAsync(grain).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var nextActivationId = await grain.GetActivationId();
        Assert.NotEqual(initialActivationId, nextActivationId);
    }

    [Fact]
    public async Task GracefulShutdownCompletesWithIdleActivations()
    {
        var activationTasks = Enumerable.Range(0, 32)
            .Select(key => GrainFactory.GetGrain<ICatalogTestGrain>(key).GetActivationId());
        await Task.WhenAll(activationTasks);

        await HostedCluster.StopAllSilosAsync().WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
    }
}
