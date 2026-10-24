using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class EarlyActivationValidationTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task StatelessWorker_FailsBeforeJournalInitializationReplayAndWork()
    {
        var grainType = typeof(StatelessActivationValidationTestGrain);
        var grain = Fixture.Client.GetGrain<IActivationValidationTestGrain>(Guid.NewGuid(), grainType.FullName!);
        await AssertRejectedBeforeRecoveryAsync(grain, grainType, "one activation");
    }

    [Theory]
    [InlineData(typeof(ReentrantActivationValidationTestGrain))]
    [InlineData(typeof(MayInterleaveActivationValidationTestGrain))]
    [InlineData(typeof(AlwaysInterleaveActivationValidationTestGrain))]
    [InlineData(typeof(MetadataReentrantActivationValidationGrain))]
    [InlineData(typeof(MetadataMayInterleaveActivationValidationGrain))]
    public async Task ReentrantAndInterleavingModels_InitializeAndReplayNormally(Type grainType)
    {
        await AssertSupportedReplayAsync(grainType);
    }

    private async Task AssertRejectedBeforeRecoveryAsync(IActivationValidationTestGrain grain, Type grainType, string diagnostic)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => grain.IncrementAsync());
        Assert.Contains(diagnostic, failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(grainType.Name, failure.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Fixture.ActivationProbe.Get(grain.GetGrainId()));
        Assert.False(observation.InstanceAvailableDuringConstruction);
        Assert.Equal(0, observation.Activated);
        Assert.Equal(0, observation.Calls);
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        Assert.Equal(0, Fixture.Storage.GetInitializationCount(journal));
        Assert.Equal(0, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, grain.GetGrainId()));
    }

    [Fact]
    public async Task SupportedModel_InitializesAndReplaysFreshActivationNormally()
    {
        await AssertSupportedReplayAsync(typeof(SupportedActivationValidationTestGrain));
    }

    [Fact]
    public async Task ResolvedNonReentrantMetadata_InitializesAndReplaysNormally()
    {
        await AssertSupportedReplayAsync(typeof(MetadataNonReentrantActivationValidationGrain));
    }

    private async Task AssertSupportedReplayAsync(Type grainType)
    {
        var grain = Fixture.Client.GetGrain<IActivationValidationTestGrain>(Guid.NewGuid(), grainType.FullName!);
        Assert.Equal(1, await grain.IncrementAsync());
        var first = Assert.Single(Fixture.ActivationProbe.Get(grain.GetGrainId()));
        Assert.False(first.InstanceAvailableDuringConstruction);
        Assert.NotNull(first.Context.GrainInstance);
        Assert.Equal(1, first.Activated);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, Fixture.Storage.GetReadCount(JournalId.FromGrainId(grain.GetGrainId())));
        await grain.DeactivateAsync();
        await first.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, await grain.IncrementAsync());
        var observations = Fixture.ActivationProbe.Get(grain.GetGrainId());
        Assert.Equal(2, observations.Length);
        Assert.NotSame(first.Context, observations[1].Context);
        Assert.Equal(1, observations[1].Activated);
        Assert.Equal(1, observations[1].Calls);
        Assert.Equal(2, Fixture.Storage.GetReadCount(JournalId.FromGrainId(grain.GetGrainId())));
    }
}
