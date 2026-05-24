#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestCategory("BVT"), TestCategory("Migration")]
public class ActivationDataMigrationTests(ActivationDataMigrationTests.Fixture fixture) : IClassFixture<ActivationDataMigrationTests.Fixture>
{
    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary;

    [Fact]
    public async Task TryStartMigration_ReturnsTrue_WhenActivationCanStartMigration()
    {
        var activation = await GetActivation();

        Assert.True(activation.TryStartMigration(requestContext: null));

        Assert.Equal(ActivationState.Deactivating, activation.State);

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TryStartMigration_ReturnsFalse_WhenActivationIsInvalid()
    {
        var activation = await GetActivation();
        var originalDeactivated = activation.Deactivated;
        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), CancellationToken.None);
        await originalDeactivated.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.False(activation.TryStartMigration(requestContext: null));
    }

    private async Task<ActivationData> GetActivation()
    {
        var grain = _fixture.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
        await grain.Nop();

        var grainId = ((GrainReference)grain).GrainId;
        var directory = PrimarySilo.SiloHost.Services.GetRequiredService<ActivationDirectory>();
        return Assert.IsType<ActivationData>(directory.FindTarget(grainId));
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
        }
    }
}
