using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class JournaledTestOutboxBehaviorTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task EquivalentDuplicates_BeforeAndAfterCommit_PreserveOneOutput()
    {
        var owner = NewGrain();
        using var original = CreateOutput(owner);
        using var copied = original with { Payload = original.Payload.Slice(0) };
        var serializer = Fixture.Client.ServiceProvider.GetRequiredService<Serializer<DurableEnvelope>>();
        using var copy = serializer.Deserialize(serializer.SerializeToArray(original));
        Assert.Same(original.Payload.First, copied.Payload.First);
        Assert.NotSame(original.Payload.First, copy.Payload.First);

        await owner.StageOutputAsync(original);
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        await owner.StageOutputAsync(copied);
        Assert.Same(retained.Payload.First, Assert.Single(Fixture.GetStagedOutput(owner)).Payload.First);
        await owner.RetryWriteStateAsync();
        await owner.StageOutputAsync(copy);
        Assert.Same(retained.Payload.First, Assert.Single(Fixture.GetStagedOutput(owner)).Payload.First);
        var before = await owner.GetSnapshotAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        await owner.StageOutputAsync(copy);

        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.MessageId, output.MessageId);
        Assert.Equal(0, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), output).Body);
        Assert.Equal(1, recovered.OutboxCount);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("body-bytes")]
    public async Task ConflictingDuplicate_RejectsChangedEnvelopeAndPreservesCommittedOutput(string difference)
    {
        var owner = NewGrain();
        using var original = CreateOutput(owner);
        await owner.StageOutputAsync(original);
        await owner.RetryWriteStateAsync();
        var retained = Assert.Single(Fixture.GetStagedOutput(owner));
        var conflicting = difference switch
        {
            "sender" => original with { SenderId = GrainId.Create("other-sender", "1") },
            "receiver" => original with { ReceiverId = GrainId.Create("other-receiver", "1") },
            _ => original with { Payload = CreateOutput(owner, changed: true).Payload }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StageOutputAsync(conflicting));

        Assert.Contains(original.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Same(retained.Payload.First, Assert.Single(Fixture.GetStagedOutput(owner)).Payload.First);
        await owner.RetryWriteStateAsync();
        await owner.RequestDeactivationAsync();
        var recovered = await owner.GetSnapshotAsync();
        Assert.Equal(1, recovered.OutboxCount);
        var output = Assert.Single(Fixture.GetStagedOutput(owner));
        Assert.Equal(original.MessageId, output.MessageId);
        Assert.Equal(original.SenderId, output.SenderId);
        Assert.Equal(original.ReceiverId, output.ReceiverId);
        Assert.Equal(0, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(), output).Body);
    }

    [Fact]
    public async Task HandlerEquivalentDuplicateOutput_CommitsOneEffectAndOneOutput()
    {
        var receiver = NewGrain();
        var target = GrainId.Create("output-target", "handler");
        using var envelope = CreateEnvelope(
            receiver,
            NewMessage(97, "duplicate-output") with { ForwardTo = target },
            "messages/duplicate-output");

        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
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
        Assert.Equal(output.MessageId, Assert.Single(Fixture.GetStagedOutput(receiver)).MessageId);
        Assert.Empty(recovered.InboxDeadLetters);
    }





    private DurableEnvelope CreateOutput(IDurableMessagingTestGrain owner, bool changed = false) =>
        TestApplicationProtocol.Create(Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>(),
            GrainId.Create("output-sender", "1"), owner.GetGrainId(), "output", changed ? 1 : 0);
}
