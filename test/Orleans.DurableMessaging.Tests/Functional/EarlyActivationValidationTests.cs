using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class EarlyActivationValidationTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(typeof(ReentrantActivationValidationTestGrain), "non-reentrant")]
    [InlineData(typeof(StatelessActivationValidationTestGrain), "one activation")]
    [InlineData(typeof(MayInterleaveActivationValidationTestGrain), "non-reentrant")]
    [InlineData(typeof(AlwaysInterleaveActivationValidationTestGrain), "interleavable method")]
    public async Task UnsupportedModel_FailsBeforeJournalInitializationReplayAndWork(Type grainType, string diagnostic)
    {
        var grain = Fixture.Client.GetGrain<IActivationValidationTestGrain>(Guid.NewGuid(), grainType.FullName!);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => grain.IncrementAsync());
        Assert.Contains(diagnostic, failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(grainType.Name, failure.ToString(), StringComparison.Ordinal);
        var observation = Assert.Single(Fixture.ActivationProbe.Get(grain.GetGrainId()));
        Assert.False(observation.InstanceAvailableDuringConstruction);
        Assert.Equal(0, observation.ReplayStarted);
        Assert.Equal(0, observation.ReplayCompleted);
        Assert.Equal(0, observation.Activated);
        Assert.Equal(0, observation.Calls);
        var journal = JournalId.FromGrainId(grain.GetGrainId());
        Assert.Equal(0, Fixture.Storage.GetInitializationCount(journal));
        Assert.Equal(0, Fixture.Storage.GetReadCount(journal));
        Assert.Equal(0, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, grain.GetGrainId()));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount("orleans.messaging.outbox-drain", grain.GetGrainId()));
    }

    [Fact]
    public async Task SupportedModel_InitializesAndReplaysFreshActivationNormally()
    {
        var grain = Fixture.Client.GetGrain<IActivationValidationTestGrain>(Guid.NewGuid(), typeof(SupportedActivationValidationTestGrain).FullName!);
        Assert.Equal(1, await grain.IncrementAsync());
        var first = Assert.Single(Fixture.ActivationProbe.Get(grain.GetGrainId()));
        Assert.False(first.InstanceAvailableDuringConstruction);
        Assert.NotNull(first.Context.GrainInstance);
        Assert.Equal(1, first.ReplayStarted);
        Assert.Equal(1, first.ReplayCompleted);
        Assert.Equal(1, first.Activated);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, Fixture.Storage.GetReadCount(JournalId.FromGrainId(grain.GetGrainId())));
        await grain.DeactivateAsync();
        await first.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, await grain.IncrementAsync());
        var observations = Fixture.ActivationProbe.Get(grain.GetGrainId());
        Assert.Equal(2, observations.Length);
        Assert.NotSame(first.Context, observations[1].Context);
        Assert.Equal(1, observations[1].ReplayStarted);
        Assert.Equal(1, observations[1].ReplayCompleted);
        Assert.Equal(1, observations[1].Activated);
        Assert.Equal(1, observations[1].Calls);
        Assert.Equal(2, Fixture.Storage.GetReadCount(JournalId.FromGrainId(grain.GetGrainId())));
    }
}
