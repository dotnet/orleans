using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class JournaledTestOutboxBehaviorTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task EquivalentDuplicates_BeforeAndAfterCommit_PreserveOneOutput()
    {
        var owner = NewGrain();
        var original = CreateOutput(owner);
        var copied = original;
        var serializer = Fixture.Client.ServiceProvider.GetRequiredService<Serializer<OutboxMessage>>();
        var copy = serializer.Deserialize(serializer.SerializeToArray(original));
        Assert.Same(original.Envelope.Payload, copied.Envelope.Payload);
        Assert.Same(original.Envelope.Headers, copied.Envelope.Headers);
        Assert.NotSame(original.Envelope.Payload, copy.Envelope.Payload);
        Assert.NotSame(original.Envelope.Headers, copy.Envelope.Headers);

        await owner.StageOutputAsync(original);
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        await owner.StageOutputAsync(copied);
        Assert.Same(retained.Envelope.Headers, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Headers);
        Assert.Same(retained.Envelope.Payload, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Payload);
        await owner.RetryWriteStateAsync();
        await owner.StageOutputAsync(copy);
        Assert.Same(retained.Envelope.Headers, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Headers);
        Assert.Same(retained.Envelope.Payload, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Payload);
        var before = await owner.GetSnapshotAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await owner.StageOutputAsync(copy);

        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.Envelope.MessageId, output.Envelope.MessageId);
        Assert.Equal(0, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), output).Body);
        Assert.Equal(1, recovered.OutboxCount);
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("body-bytes")]
    public async Task ConflictingDuplicate_RejectsChangedEnvelopeAndPreservesCommittedOutput(string difference)
    {
        var owner = NewGrain();
        var original = CreateOutput(owner);
        await owner.StageOutputAsync(original);
        await owner.RetryWriteStateAsync();
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        var conflicting = difference switch
        {
            "receiver" => original with { ReceiverId = GrainId.Create("other-receiver", "1") },
            _ => original with
            {
                Envelope = TestApplicationProtocol.With(original.ToInboxMessage(), Sessions,
                payload: CreateOutput(owner, changed: true).Envelope.Payload).Envelope
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StageOutputAsync(conflicting));

        Assert.Contains(original.Envelope.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Same(retained.Envelope.Headers, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Headers);
        Assert.Same(retained.Envelope.Payload, Assert.Single(Fixture.GetStagedOutput(owner)).Envelope.Payload);
        await owner.RetryWriteStateAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.Equal(1, recovered.OutboxCount);
        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.Envelope.MessageId, output.Envelope.MessageId);
        Assert.Equal(TestApplicationProtocol.Sender(Sessions, original.Envelope), TestApplicationProtocol.Sender(Sessions, output.Envelope));
        Assert.Equal(original.ReceiverId, output.ReceiverId);
        Assert.Equal(0, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), output).Body);
    }

    [Fact]
    public async Task HandlerEquivalentDuplicateOutput_CommitsOneEffectAndOneOutput()
    {
        var receiver = NewGrain();
        var target = GrainId.Create("output-target", "handler");
        var envelope = CreateEnvelope(
            receiver,
            NewMessage(97, "duplicate-output") with { ForwardTo = target },
            "messages/duplicate-output");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            static snapshot => snapshot.InboxCount == 0 && (snapshot.Effects.Count > 0 || snapshot.InboxDeadLetters.Count > 0));

        Assert.Empty(completed.InboxDeadLetters);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(1, completed.OutboxCount);
        var output = Assert.Single(Fixture.GetStagedOutput(receiver));
        Assert.Equal(target, output.ReceiverId);
        await receiver.RequestDeactivationAsync();
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(output.Envelope.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).Envelope.MessageId);
        Assert.Empty(recovered.InboxDeadLetters);
    }





    private OutboxMessage CreateOutput(IMessagingTestGrain owner, bool changed = false) =>
        TestApplicationProtocol.Create(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(),
            owner.GetGrainId(), owner.GetGrainId(), "output", changed ? 1 : 0);
}
