using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Durable.Messaging.Tests.Functional;

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
        await AssertRejectedBeforeRecoveryAsync(grain, "one activation");
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

    [Theory]
    [InlineData(typeof(InboxlessDurableGrain))]
    [InlineData(typeof(InboxlessPlainGrain))]
    public async Task Delivery_ToActivationWhichNeverResolvedItsInbox_Fails(Type grainType)
    {
        var grain = Fixture.Client.GetGrain<IInboxlessGrain>(Guid.NewGuid(), grainType.FullName!);
        await grain.PingAsync();
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var message = TestApplicationProtocol.CreateInbox(
            sessions, GrainId.Create("external-test-sender", "inboxless"), "messages/record", "payload", Fixture.JobNow);

        await Assert.ThrowsAsync<GrainExtensionNotInstalledException>(
            () => grain.AsReference<IMessagingExtension>().DeliverAsync(message, TestContext.Current.CancellationToken).AsTask());
    }

    private async Task AssertRejectedBeforeRecoveryAsync(IActivationValidationTestGrain grain, string diagnostic)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => grain.IncrementAsync());
        Assert.Contains(diagnostic, failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(grain.GetGrainId().Type.ToString(), failure.ToString(), StringComparison.Ordinal);
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
