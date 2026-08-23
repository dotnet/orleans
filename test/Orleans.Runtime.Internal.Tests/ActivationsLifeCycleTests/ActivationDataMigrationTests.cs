#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Migration")]
public class ActivationDataMigrationTests(ActivationDataMigrationTests.Fixture fixture) : IClassFixture<ActivationDataMigrationTests.Fixture>
{
    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary!;

    [Fact]
    public async Task TryStartMigration_ReturnsTrue_WhenActivationCanStartMigration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);

        Assert.True(activation.TryStartMigration(requestContext: null, cancellationToken));

        Assert.Equal(ActivationState.Deactivating, activation.State);

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    [Fact]
    public async Task TryStartMigration_ReturnsFalse_WhenActivationIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        var originalDeactivated = activation.Deactivated;
        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, "test"), cancellationToken);
        await originalDeactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.False(activation.TryStartMigration(requestContext: null, cancellationToken));
    }

    [Fact]
    public async Task TryStartMigration_DoesNotAcquireActivationInstanceMonitor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var activation = await GetActivation(cancellationToken);
        Assert.NotSame(activation, activation.SynchronizationLock);
        Assert.Same(activation.SynchronizationLock, activation.SynchronizationLock);

        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLock = new ManualResetEventSlim();
        var lockHolder = Task.Factory.StartNew(
            () =>
            {
                lock (activation)
                {
                    lockAcquired.SetResult();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                    {
                        throw new TimeoutException("Timed out waiting to release the activation instance monitor.");
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        try
        {
            Assert.True(
                await Task.Run(
                    () => activation.TryStartMigration(requestContext: null, cancellationToken),
                    cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }
        finally
        {
            releaseLock.Set();
            await lockHolder.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        await activation.Deactivated.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private async Task<ActivationData> GetActivation(CancellationToken cancellationToken)
    {
        var grain = _fixture.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
        await grain.Nop().WaitAsync(cancellationToken);

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
